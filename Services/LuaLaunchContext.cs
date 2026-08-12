namespace SoulBuddy.Services;

internal static class LuaLaunchContext
{
    public static bool FromLua { get; private set; }
    public static string? Token { get; private set; }
    public static string? SafeToken { get; private set; }

    public static void Initialize(IReadOnlyList<string> args)
    {
        FromLua = args.Any(argument =>
            string.Equals(argument, "--from-lua", StringComparison.OrdinalIgnoreCase));

        string? token = null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--lua-token", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Count)
                    token = args[index + 1];
                break;
            }

            const string prefix = "--lua-token=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                token = argument[prefix.Length..];
                break;
            }
        }

        Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        SafeToken = Token is null ? null : SanitizeToken(Token);
    }

    public static string ScopePath(string path)
    {
        if (string.IsNullOrWhiteSpace(SafeToken))
            return path;

        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var scopedFileName = $"{stem}.{SafeToken}{extension}";

        return string.IsNullOrWhiteSpace(directory)
            ? scopedFileName
            : Path.Combine(directory, scopedFileName);
    }

    public static string InstanceName(string baseName) =>
        string.IsNullOrWhiteSpace(SafeToken)
            ? baseName
            : $"{baseName}.{SafeToken}";

    private static string SanitizeToken(string token)
    {
        var characters = token
            .Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '_')
            .ToArray();

        var sanitized = new string(characters);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "lua"
            : sanitized;
    }
}
