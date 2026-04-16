using System;
using System.Collections.Generic;

namespace XASlave.Services;

public readonly record struct CharacterAliasIdentity(
    string NameWorld,
    string Name,
    string World);

public static class CharacterAliasHelper
{
    private static readonly string[] AliasFirstNames =
        """
        Bash,Tmux,Fzf,Bat,Ripgrep,Ls,Cat,Grep,Sed,Awk,Just,Entr,Task,Gh,Git,Docker,Kubectl,Helm,Ansible,Terraform,Zoxide,Tldr,Pet,Navi,Du,Dufa,Glances,Htop,Btop,Yazi,Fd,Dua,Atuin,Zellij,Wezterm,Neovim,Vim,Emacs,Nano,Make,Cargo,Npm,Yarn,Pip,Conda,Brew,Apt,Yum,Rpm,Ssh,Scp,Rsync,Curl,Wget,Tar,Gzip,Unzip,Find,Locate,Tree,Df,Free,Top,Ps,Kill,Nice,Renice,Echo,Printf,Read,Sleep,Wait,Trap,Source,Export,Alias,Func,Test,True,False,Jq,Exa,Lsd,Delta,Difft,Hyperfine,Tokei,Dust,Ncdu,Gotop,Bottom,Stern,Kubectx,Kubens,Flux,Argo,Tekton,Jenkins,Circleci,Aws,Az,Gcloud,Pulumi,Chef,Puppet,Salt,Vagrant,Packer,Nomad,Consul,Vault,Serf,Fabio,Traefik,Nginx,Apache,Httpie,Curlie,Aria2,Ffmpeg,Sox,Pandoc,Ag,Pt,Uc
        """
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly string[] AliasLastNames =
        """
        Python,Java,Rust,Go,Ruby,Perl,Lua,Kotlin,Dart,Swift,Scala,Clojure,Haskell,Elixir,Erlang,Zig,Nim,Crystal,Julia,Forth,Prolog,Bash,C,Pascal,Basic,Ada,Cobra,Scheme,Smalltalk,Tcl,Awk,Sed,Groovy,Wren,Janet,Fennel,Hare,Odin,Carp,Beef,Jai,Hylo,Lobster,Haxe,Elm,Idris,Agda,Lean,Coq,Ocaml,Fsharp,Vbnet,Php,Sql,Html,Css,Json,Yaml,Toml,Markdown,Assembly,Fortran,Cobol,Algol,Racket,Guile,Chicken,Pico,Logo,Scratch,Euphoria,Factor,Joy,Csharp,JavaScript,TypeScript,CoffeeScript,ActionScript,ObjectiveC,Ceylon,Fantom,Gosu,Xtend,Xtext,Ballerina,Pony,Red,Rebol,Self,Io,Ioke,Slate,Newspeak,Pharo,Squeak,GemStone,Actor,Eiffel,Sather,Beta,Simula,Modula,Oberon,Delphi,ObjectPascal,Spark,PlSql,TSql,PowerShell,Shell,Fish,Zsh,Ksh,Csh,Tcsh,Dash
        """
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly string[] AliasWorldNames =
        """
        Ifrit,Shiva,Ramuh,Titan,Bahamut,Leviathan,Odin,Phoenix,Garuda,Valefor,Alexander,Carbuncle,Diablos,Cactuar,Tonberry,Chocobo,Anima,Yojimbo,Eden,Fenrir,Ultima,Quetzalcoatl,Siren,Atomos,Cerberus,Doomtrain,Pandemonium,Ark,Syldra,Goblin,Flan,Bom,Sahagin,Mandragora,Morbol,Behemoth,Coeurl,Ahriman,Malboro,IronGiant,Deathgaze,Omega,Neo,Magus,Sisters,Knights,Round,Typhon,Belias,Mateus,Shemhazai,Hashmal,Famfrit,Adrammelech,Zalera,Zeromus,Exodus,Chaos,Asura,Lakshmi,Hecatoncheir,Brynhildr,Golem,Unicorn,Sylph,Remora,Kirin,Cait,Moogle,Bomb,Bismarck,Hades,Lich,Marilith,Kraken,Tiamat,Rubicant,Cagnazzo,Barbariccia,Scarmiglione,Mist,Imp,Sprite,Fairy,Sylphid,Efreet,Jinn,Titanus,Catoblepas,Holy,Mega,Flare,Meteor,Quake,Tornado,Flood,Bio,Demi,Break,Doom,Death,Zombie,Phantom,Ghost,Specter,Wraith,Eidolon,Esper,Aeon,Eikon,Primal,Astral,Avatar,Guardian,Force
        """
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly Dictionary<string, CharacterAliasIdentity> AliasCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> WorldAliasCache = new(StringComparer.OrdinalIgnoreCase);

    public static CharacterAliasIdentity Resolve(string? characterNameWorld)
    {
        SplitNameWorldKey(characterNameWorld, out var originalName, out var originalWorld);
        return Resolve(originalName, originalWorld);
    }

    public static CharacterAliasIdentity Resolve(string? originalName, string? originalWorld, ulong stableId = 0)
    {
        var normalizedName = NormalizeIdentityPart(originalName);
        var normalizedWorld = NormalizeIdentityPart(originalWorld);
        var cacheKey = BuildCacheKey(normalizedName, normalizedWorld, stableId);
        if (AliasCache.TryGetValue(cacheKey, out var alias))
            return alias;

        var nameSeed = normalizedName.Length > 0
            ? normalizedName
            : $"unknown-name:{stableId}";
        var worldSeed = normalizedWorld.Length > 0
            ? normalizedWorld
            : normalizedName.Length > 0
                ? normalizedName
                : $"unknown-world:{stableId}";

        var nameHash = ComputeDeterministicHash(nameSeed);
        var firstName = AliasFirstNames[(int)(nameHash % (uint)AliasFirstNames.Length)];
        var lastName = AliasLastNames[(int)(nameHash % (uint)AliasLastNames.Length)];
        var worldName = ResolveWorldAlias(worldSeed);
        var aliasName = $"{firstName} {lastName}";

        alias = new CharacterAliasIdentity($"{aliasName}@{worldName}", aliasName, worldName);
        AliasCache[cacheKey] = alias;
        return alias;
    }

    public static string ResolveWorldAlias(string? originalWorld)
    {
        var normalizedWorld = NormalizeIdentityPart(originalWorld);
        var cacheKey = normalizedWorld.Length > 0 ? normalizedWorld : "unknown";
        if (WorldAliasCache.TryGetValue(cacheKey, out var alias))
            return alias;

        var seed = normalizedWorld.Length > 0 ? normalizedWorld : "unknown-world";
        var hash = ComputeDeterministicHash(seed);
        alias = AliasWorldNames[(int)(hash % (uint)AliasWorldNames.Length)];
        WorldAliasCache[cacheKey] = alias;
        return alias;
    }

    public static string GetNameFromKey(string? characterNameWorld)
    {
        SplitNameWorldKey(characterNameWorld, out var characterName, out _);
        return characterName;
    }

    public static string GetWorldFromKey(string? characterNameWorld)
    {
        SplitNameWorldKey(characterNameWorld, out _, out var world);
        return world;
    }

    public static void SplitNameWorldKey(string? characterNameWorld, out string characterName, out string world)
    {
        var normalized = NormalizeNameWorldKey(characterNameWorld);
        var separatorIndex = normalized.IndexOf('@');
        if (separatorIndex < 0)
        {
            characterName = normalized;
            world = string.Empty;
            return;
        }

        characterName = normalized[..separatorIndex].Trim();
        world = normalized[(separatorIndex + 1)..].Trim();
    }

    public static string NormalizeNameWorldKey(string? characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return string.Empty;

        var trimmed = characterNameWorld.Trim();
        var separatorIndex = trimmed.IndexOf('@');
        if (separatorIndex < 0)
            return NormalizeIdentityPart(trimmed);

        var characterName = NormalizeIdentityPart(trimmed[..separatorIndex]);
        var world = NormalizeIdentityPart(trimmed[(separatorIndex + 1)..]);
        return world.Length > 0 ? $"{characterName}@{world}" : characterName;
    }

    public static string NormalizeIdentityPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildCacheKey(string originalName, string originalWorld, ulong stableId)
    {
        if (!string.IsNullOrWhiteSpace(originalName) || !string.IsNullOrWhiteSpace(originalWorld))
            return $"{originalName}@{originalWorld}";

        return $"unknown:{stableId}";
    }

    private static uint ComputeDeterministicHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= char.ToUpperInvariant(ch);
                hash *= 16777619u;
            }

            return hash;
        }
    }
}
