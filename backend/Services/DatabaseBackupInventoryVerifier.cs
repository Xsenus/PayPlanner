namespace PayPlanner.Api.Services;

/// <summary>Compares inventory facts with one read-only database view without loading either full list.</summary>
public static class DatabaseBackupInventoryVerifier
{
    public static async Task VerifyAsync(Stream inventory, DatabaseBackupSnapshotSession database,
        CancellationToken cancellationToken)
    {
        await using var actual = DatabaseBackupInventoryReader.ReadAsync(inventory, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await foreach (var expected in DatabaseBackupSnapshotInventory.ReadStoredFilesAsync(database, cancellationToken))
        {
            if (!await actual.MoveNextAsync() || actual.Current.StoredFile != expected || actual.Current.Replica is not null)
                throw Mismatch();
        }
        await foreach (var expected in DatabaseBackupSnapshotInventory.ReadStorageReplicasAsync(database, cancellationToken))
        {
            if (!await actual.MoveNextAsync() || actual.Current.Replica != expected || actual.Current.StoredFile is not null)
                throw Mismatch();
        }
        // Exhausting the reader also checks the declared counts and complete JSON footer.
        if (await actual.MoveNextAsync()) throw Mismatch();
    }

    private static InvalidDataException Mismatch()
        => new("Перечень документов или версий файлов не соответствует восстановленной базе данных.");
}
