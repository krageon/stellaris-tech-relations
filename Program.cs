using System.IO.Compression;
using System.Text;
using System.Reflection;

using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

using CWTools.Common;
using CWTools.Games;
using CWTools.Validation;
using static CWTools.Games.Files;

using StringDict = System.Collections.Generic.Dictionary<string, string>;

using YamlDeserializer = YamlDotNet.Serialization.Deserializer;
using YamlSerializerBuilder = YamlDotNet.Serialization.SerializerBuilder;
using IYamlSerializer = YamlDotNet.Serialization.ISerializer;

using CWNode = CWTools.Process.Node;
using CWValue = CWTools.Parser.Types.Value;
using SECData = CWTools.Games.ScriptedEffectComputedData;

// Add support for codepage 1252, used by CWTools
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
YamlDeserializer deserializer = new();


#region Read parameters

Options options = deserializer.Deserialize<Options>(File.ReadAllText("./config.yml"));

string gamePath = Path.GetFullPath(options.GameDir);
DirectoryInfo outDir = new(options.OutDir);

try {
	outDir.Delete(true);
} catch (DirectoryNotFoundException) {
}

outDir.Create();

bool hasMods = options.ModPaths.Count > 0;
List<WorkspaceDirectoryInput> resourceDirs = [
	WorkspaceDirectoryInput.NewWD(new WorkspaceDirectory(
		gamePath, "stellaris"
	)),
	.. options.ModPaths.Select(i => {
		string path = Path.GetFullPath(i);

		if (i[^1] is '/' or '\\') {
			return WorkspaceDirectoryInput.NewWD(new WorkspaceDirectory(
				path, Path.GetDirectoryName(path)
			));
		} else if (Path.GetExtension(path) == ".zip") {
			string root = path.Replace('\\', '/');
			return WorkspaceDirectoryInput.NewZD(new(
				path,
				Path.GetFileName(path),
				ZipFile.OpenRead(path).Entries
					.Select(i => Tuple.Create($"{root}/{i.FullName}", i.Open().ReadToString()))
					.ToFSharpList()
			));
		}

		throw new InvalidDataException($"Ill-formed mod path: {i}");
	})
];

#endregion

#region Load Data

CWTools.Games.Stellaris.STLGame stellaris = new(new GameSetupSettings<STLLookup>(
	resourceDirs.ToFSharpList(),
	EmbeddedSetupSettings.NewFromConfig(
		FSharpList<Tuple<string, string>>.Empty,
		FSharpList<Tuple<Resource, Entity>>.Empty
	),
	new ValidationSettings(LangHelpers.allSTLLangs, false, true),
	FSharpOption<RulesSettings>.Some(new(
		Directory
			.EnumerateFiles(options.ConfigDir, "*.???", SearchOption.AllDirectories)
			.Where(i => i.EndsWith(".cwt") || i.EndsWith(".log"))
			.Select(i => Tuple.Create(i, File.ReadAllText(i)))
			.ToFSharpList(),
		false,
		false,
		false
	)),
	null,
	null,
	null,
	null
));

var game = (GameObject<SECData, STLLookup>) typeof(CWTools.Games.Stellaris.STLGame)
	.GetField("game", BindingFlags.Instance | BindingFlags.NonPublic)!
	.GetValue(stellaris)!;

EntitySet<SECData> entities = new(game.Resources.AllEntities.Invoke(null));

Console.WriteLine("Loaded game data");

#endregion

#region Analyze technologies

CWComparer cwComparer = new(resourceDirs);

Dictionary<string, CWValue> variables = entities
	.AllOfType(STLConstants.EntityType.ScriptedVariables)
	.Select(i => i.Item1)
	.PipeIf(hasMods, x => x.ToSortedList(cwComparer))
	.SelectMany(i => i.Leaves)
	.ToDictionaryOverwriting(i => i.Key, i => i.Value);

Dictionary<string, Tech> techs = entities
	.AllOfType(STLConstants.EntityType.Technology)
	.Select(i => i.Item1)
	.PipeIf(hasMods, x => x.ToSortedList(cwComparer))
	.SelectMany(i => i.Children)
	.ToDictionaryOverwriting(
		i => i.Key,
		i => {
			bool vanilla = cwComparer.IsVanilla(i.Position);
			Area area = Enum.Parse<Area>(i.Tag("area").Value.ToRawString(), true);

			string tierStr = i.Tag("tier").Value.ToRawString();
			if (!int.TryParse(tierStr, out int tier)) {
				if (!tierStr.StartsWith('@')) {
					throw new NotSupportedException($"Unexpected tier value: {tierStr}");
				}

				tier = ((CWValue.Int) variables[tierStr]).Item;
			}

			bool dangerous = i.Tag("is_dangerous")
				.Map(x => x.ToRawString().Equals("yes", StringComparison.OrdinalIgnoreCase))
				.UnwrapOr(false);
			bool rare = i.Tag("is_rare")
				.Map(x => x.ToRawString().Equals("yes", StringComparison.OrdinalIgnoreCase))
				.UnwrapOr(false);

			List<ITechRequirement> requires = i.Child("prerequisites")
				.Map(x => {
					var list = x.LeafValues
						.Select(x => new TechRequirementSingle(x.Value.ToRawString()) as ITechRequirement)
						.ToList();

					foreach (CWNode clause in x.Children) {
						if (!string.Equals(clause.Key, "OR", StringComparison.OrdinalIgnoreCase)) {
							throw new NotSupportedException();
						}

						list.Add(new TechRequirementAlternatives(
							clause.LeafValues.Select(i => i.Value.ToRawString())
						));
					}

					return list;
				})
				.UnwrapOr([]);


			Dictionary<string, Area> swaps = [];
			foreach (CWNode node in i.Childs("technology_swap")) {
				swaps[node.Tag("name").Value.ToRawString()] =
					node.Tag("area").Map(x => Enum.Parse<Area>(x.ToRawString(), true)).UnwrapOr(area);
			}

			return new Tech(vanilla, area, tier, dangerous, rare, requires, swaps);
		}
	);

foreach ((string id, Tech tech) in techs) {
	foreach (ITechRequirement requirement in tech.Requires) {
		foreach (string req in requirement.Requirements()) {
			techs[req].Unlocks.Add(id);
		}
	}
}

Console.WriteLine($"Analyzed {techs.Count} technologies");

#endregion

#region Load localizations

Dictionary<Lang, Dictionary<string, Lazy<string>>> locs = game.LocalisationManager.LocalisationEntries()
	.ToDictionary(
		x => x.Item1,
		x => x.Item2
			.AsEnumerable()
			.PipeIf(hasMods, x => x.ToSortedList((x, y) => cwComparer.Compare(x.Item2, y.Item2)))
			.ToDictionaryOverwriting(
				i => i.Item1,
				i => new Lazy<string>(() => {
					string value = i.Item2.desc;
					try {
						return deserializer.Deserialize<string>(value);
					} catch (YamlDotNet.Core.YamlException) {
						// PDX doesn't use "standard" YAML.
						// For example quotes within quotes with no escapes.
						// Hence just do naive process as fallback here.
						return value.StartsWith('"') && value.EndsWith('"')
							? value[1..^1]
							: value;
					}
				})
			)
	);

Console.WriteLine("Loaded localization");

#endregion

#region Emit localizations

Dictionary<string, StringDict> generatedLocs = LangHelpers.allSTLLangs.ToDictionary(
	lang => lang.LangId(),
	lang => techs.Aggregate(
		new StringDict(),
		(dict, i) => {
			(string id, Tech tech) = i;

			StringBuilder sb = new();
			void BuildRelatedTechString(string techId, int indent) {
				Tech tech = techs[techId];

				sb.Append("\n$");
				for (int i = 0; i < indent; i++) {
					sb.Append('t');
				}

				sb.Append($"${LocConsts.Bullet}");

				if (techs[id].Vanilla && !tech.Vanilla) {
					sb.Append(LocConsts.Mod);
				}

				sb.Append("['technology:")
					.Append(techId)
					.Append("']");

				if (tech.Dangerous || tech.Rare) {
					sb.Append(LocConsts.LParen);

					if (tech.Dangerous) {
						sb.Append(LocConsts.Dangerous);
					}

					if (tech.Dangerous && tech.Rare) {
						sb.Append(LocConsts.Sep);
					}

					if (tech.Rare) {
						sb.Append(LocConsts.Rare);
					}

					sb.Append(LocConsts.RParen);
				}
			}

			if (tech.Requires.Count > 0) {
				sb.Append($"\n\n{LocConsts.Requires}");
				foreach (ITechRequirement require in tech.Requires) {
					if (require is TechRequirementSingle single) {
						BuildRelatedTechString(single.Id, 1);
					} else if (require is TechRequirementAlternatives alternatives) {
						sb.Append($"\n$t${LocConsts.Bullet}{LocConsts.OneOf}");
						foreach (string alternative in alternatives.Alternatives) {
							BuildRelatedTechString(alternative, 2);
						}
					} else {
						throw new NotSupportedException();
					}
				}
			}

			if (tech.Unlocks.Count > 0) {
				sb.Append($"\n\n{LocConsts.Unlocks}");
				foreach (string unlock in tech.Unlocks) {
					BuildRelatedTechString(unlock, 1);
				}
			}

			string content = sb.ToString();

			foreach ((string techId, Area area) in tech.Swaps.Prepend(new(id, tech.Area))) {
				string key = $"{techId}_desc";
				if (locs[lang].TryGetValue(key, out Lazy<string>? desc)) {
					dict[key] = desc.Value
						+ $"\n\n£{area.ToString().ToLowerInvariant()}£ §Y${area.ToString().ToUpperInvariant()}$ T{tech.Tier}§!"
						+ content;
				}
			}

			return dict;
		}
	)
);

Console.WriteLine($"Generated {generatedLocs.Sum(i => i.Value.Count)} localization entries");

IYamlSerializer serializer = new YamlSerializerBuilder()
	.WithDefaultScalarStyle(YamlDotNet.Core.ScalarStyle.DoubleQuoted)
	.Build();
Encoding utf8bom = new UTF8Encoding(true);
foreach ((string lang, StringDict loc) in generatedLocs) {
	// We use partly StringBuilder and partly YamlSerializer here.
	// Since we want:
	//   1. Never quote keys
	//   2. Always quote values
	StringBuilder sb = new($"l_{lang}:\n");

	foreach ((string key, string value) in loc) {
		sb.Append(' ')
			.Append(key)
			.Append(": ")
			.Append(serializer.Serialize(value));
	}

	File.WriteAllText(
		$"{outDir.FullName}/techrel_l_{lang}.yml",
		sb.ToString(),
		utf8bom
	);
}

Console.WriteLine($"Wrote {generatedLocs.Count} language files");

#endregion


#region Data models

public sealed class Options {
	private string? gameDir;

	public string GameDir {
		get => gameDir ?? throw new InvalidDataException("Game dir not specified");
		set => gameDir = value;
	}

	public string ConfigDir { get; set; } = "./cwtools-stellaris-config/config/";

	public string OutDir { get; set; } = "./out";

	public List<string> ModPaths { get; set; } = [];
}

public class Tech(
	bool vanilla,
	Area area,
	int tier,
	bool dangerous,
	bool rare,
	List<ITechRequirement> requires,
	Dictionary<string, Area> swaps
) {
	public bool Vanilla { get; private init; } = vanilla;
	public Area Area { get; private init; } = area;
	public int Tier { get; private init; } = tier;
	public bool Dangerous { get; private init; } = dangerous;
	public bool Rare { get; private init; } = rare;

	public List<ITechRequirement> Requires { get; private init; } = requires;
	public List<string> Unlocks { get; private init; } = [];
	public Dictionary<string, Area> Swaps { get; private init; } = swaps;
}

public enum Area {
	Physics,
	Society,
	Engineering,
}

public interface ITechRequirement {
	public IEnumerable<string> Requirements();
}

public class TechRequirementSingle(string id) : ITechRequirement {
	public string Id { get; private init; } = id;

	public IEnumerable<string> Requirements() => [Id];
}

public class TechRequirementAlternatives(IEnumerable<string> alternatives) : ITechRequirement {
	public string[] Alternatives { get; private init; } = alternatives.ToArray();

	public IEnumerable<string> Requirements() => Alternatives;
}

#endregion

#region Utilities

public class CWComparer(List<WorkspaceDirectoryInput> inputs) : IComparer<CWNode>, IComparer<CWTools.Localisation.Entry>, IComparer<CWTools.Utilities.Position.range> {
	public string GamePath { get; private init; } = ((WorkspaceDirectoryInput.WD) inputs[0]).Item.path;

	public string[] ModPaths { get; private init; } = [.. inputs.Skip(1).Select(
		x => x switch {
			WorkspaceDirectoryInput.WD wd => wd.Item.path,
			WorkspaceDirectoryInput.ZD zd => zd.Item.path,
			_ => throw new NotImplementedException(),
		}
	)];

	public bool IsVanilla(CWTools.Utilities.Position.range range) =>
		range.FileName.StartsWith(GamePath);

	public int ModIndex(CWTools.Utilities.Position.range range) =>
		ModPaths.Index().First((i) => range.FileName.StartsWith(i.Item)).Index;

	public int Compare(CWNode? x, CWNode? y) {
		if (x == null) {
			return y == null ? 0 : -1;
		} else if (y == null) {
			return 1;
		}

		return Compare(x.Position, y.Position);
	}

	public int Compare(CWTools.Localisation.Entry x, CWTools.Localisation.Entry y) =>
		Compare(x.position, y.position);

	public int Compare(CWTools.Utilities.Position.range x, CWTools.Utilities.Position.range y) {
		bool isVanillaX = IsVanilla(x);
		bool isVanillaY = IsVanilla(y);

		if (isVanillaX) {
			return isVanillaY
				? string.Compare(
					x.FileName,
					y.FileName,
					StringComparison.OrdinalIgnoreCase
				)
				: -1;
		} else if (isVanillaY) {
			return 1;
		}

		int modIndexX = ModIndex(x);
		int modIndexY = ModIndex(y);
		return (modIndexX - modIndexY) switch {
			0 => string.Compare(
				x.FileName[ModPaths[modIndexX].Length..],
				y.FileName[ModPaths[modIndexY].Length..],
				StringComparison.OrdinalIgnoreCase
			),
			int i => i
		};
	}
}

public static class Extensions {
	public static T PipeIf<T>(this T self, bool doPipe, Func<T, T> mapper) =>
		doPipe ? mapper(self) : self;

	public static string ReadToString(this Stream self) {
		using Stream stream = self;
		using StreamReader sr = new(stream, true);
		return sr.ReadToEnd();
	}

	public static FSharpList<T> ToFSharpList<T>(this IEnumerable<T> self) =>
		ListModule.OfSeq(self);

	public static Dictionary<TKey, TValue> ToDictionaryOverwriting<T, TKey, TValue>(this IEnumerable<T> self, Func<T, TKey> keySelector, Func<T, TValue> valueSelector) where TKey : notnull {
		Dictionary<TKey, TValue> dict = [];

		IEnumerator<T> enumerator = self.GetEnumerator();
		while (enumerator.MoveNext()) {
			dict[keySelector(enumerator.Current)] = valueSelector(enumerator.Current);
		}

		return dict;
	}

	public static List<T> ToSortedList<T>(this IEnumerable<T> self, IComparer<T> comparer) {
		List<T> list = [.. self];
		list.Sort(comparer);
		return list;
	}

	public static List<T> ToSortedList<T>(this IEnumerable<T> self, Comparison<T> comparison) {
		List<T> list = [.. self];
		list.Sort(comparison);
		return list;
	}

	public static FSharpOption<TTo> Map<T, TTo>(this FSharpOption<T> self, Func<T, TTo> mapper) =>
		OptionModule.IsSome(self)
			? FSharpOption<TTo>.Some(mapper.Invoke(self.Value))
			: FSharpOption<TTo>.None;

	public static T UnwrapOr<T>(this FSharpOption<T> self, T fallback) =>
		OptionModule.IsSome(self)
			? self.Value
			: fallback;

	public static string LangId(this Lang self) =>
		self is Lang.STL lang
			? lang.Item switch {
				STLLang.English => "english",
				STLLang.French => "french",
				STLLang.German => "german",
				STLLang.Spanish => "spanish",
				STLLang.Russian => "russian",
				STLLang.Polish => "polish",
				STLLang.Braz_Por => "braz_por",
				STLLang.Chinese => "simp_chinese",
				STLLang.Japanese => "japanese",
				STLLang.Korean => "korean",
				_ => throw new NotSupportedException()
			}
			: throw new NotSupportedException();
}

public static class LocConsts {
	public const string Requires = "$techrel_requires$";
	public const string Unlocks = "$techrel_unlocks$";

	public const string Bullet = "$BULLET_POINT$";
	public const string Mod = "[MOD] ";
	public const string LParen = " (";
	public const char RParen = ')';
	public const string Sep = " | ";
	public const string Dangerous = "$TECH_IS_DANGEROUS$";
	public const string Rare = "$TECH_IS_RARE$";
	public const string OneOf = "$techrel_one_of$";
}

#endregion
