using System.Reflection;

namespace Contrapost.Infrastructure.Persistence;

/// <summary>Reads SQL files from db/ that are embedded into this assembly at build time.</summary>
internal static class SqlScripts
{
    public static string Read(string name)
    {
        var resource = $"Contrapost.Infrastructure.Sql.{name}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded SQL script '{resource}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
