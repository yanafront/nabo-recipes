using Npgsql;
namespace Nabo.Recipes;

public static class DatabaseConnection
{
    public static string Parse(string value)
    {
        if (!value.StartsWith("postgres://") && !value.StartsWith("postgresql://")) return new NpgsqlConnectionStringBuilder(value).ConnectionString;
        var uri = new Uri(value);
        var credentials = uri.UserInfo.Split(':', 2);
        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "",
            Timeout = 15,
            CommandTimeout = 30
        };
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                cs.SslMode = Enum.Parse<SslMode>(pair[1], true);
        }
        return cs.ConnectionString;
    }
}
