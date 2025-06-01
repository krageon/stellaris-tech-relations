# Stellaris Tech Relations

> Technology localization generator for Stellaris that appends tech tiers and related techs in original description.

## Prebuilt Release Installation

1. Download zip file from [release page](https://github.com/Clazex/stellaris-tech-relations/releases).
2. Unpack into the [mod folder](https://stellaris.paradoxwikis.com/Modding#Mod_folder_location).

## Cloning

Make sure that submodules are initialized when cloning:

```bash
git clone --recurse-submodules
```

Or do it while afterwards:

```bash
git submodule update --init
```

## Usage

1. Copy `config.example.yml`, rename to `config.yml` and fill the fields.
2. Run the generator: `dotnet run -c Release` (takes ~1min for vanilla).
3. Create `Tech-Relations.mod` in the mod folder containing the following content:

```
name = "Tech Relations"
path = "<Project folder>/mod"
```

4. Repeat 2. to update.
