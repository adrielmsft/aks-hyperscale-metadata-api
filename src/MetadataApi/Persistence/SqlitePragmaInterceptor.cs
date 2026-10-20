using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MetadataApi.Persistence;

internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Configure(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Microsoft.Data.Sqlite executes async commands synchronously as well.
        Configure(connection);
        return Task.CompletedTask;
    }

    private static void Configure(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = MetadataDatabase.LockTimeoutSeconds;
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();

        command.CommandText = "PRAGMA foreign_keys;";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1)
        {
            throw new InvalidOperationException("SQLite foreign-key enforcement could not be enabled.");
        }

        command.CommandText = "PRAGMA synchronous;";
        if (Convert.ToInt64(command.ExecuteScalar()) != 2)
        {
            throw new InvalidOperationException("SQLite FULL synchronization could not be enabled.");
        }
    }
}
