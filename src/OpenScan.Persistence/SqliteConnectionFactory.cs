using Microsoft.Data.Sqlite;

namespace OpenScan.Persistence;

public sealed class SqliteConnectionFactory(string connectionString)
{
    public SqliteConnection Create()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
