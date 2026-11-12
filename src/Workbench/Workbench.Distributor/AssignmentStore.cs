using System.Globalization;
using Microsoft.Data.Sqlite;
using Workbench.Contracts;

namespace Workbench.Distributor;

public sealed class DistributionConflictException(string message) : Exception(message);

public sealed class AssignmentStore
{
    private readonly string connectionString;
    private readonly TimeProvider clock;
    private static readonly TimeSpan HeartbeatLimit = TimeSpan.FromSeconds(90);

    public AssignmentStore(string path, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            DefaultTimeout = 10,
            Pooling = false
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS assignments (
                id TEXT PRIMARY KEY,
                workbench TEXT NOT NULL,
                request TEXT NOT NULL,
                submitted TEXT NOT NULL,
                claim TEXT,
                heartbeat TEXT,
                result TEXT
            );
            CREATE INDEX IF NOT EXISTS assignments_workbench ON assignments(workbench, submitted);
            CREATE UNIQUE INDEX IF NOT EXISTS assignments_claim ON assignments(claim) WHERE claim IS NOT NULL;
            """;
        command.ExecuteNonQuery();
    }

    public void Submit(DispatchRequest dispatch)
    {
        dispatch.Validate();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO assignments(id, workbench, request, submitted)
            VALUES ($id, $workbench, $request, $submitted) ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", dispatch.Request.Assignment.Id);
        command.Parameters.AddWithValue("$workbench", dispatch.WorkbenchId);
        command.Parameters.AddWithValue("$request", Wire.Write(dispatch));
        command.Parameters.AddWithValue("$submitted", Timestamp());
        if (command.ExecuteNonQuery() != 1)
            throw new DistributionConflictException("Assignment ID already exists; inspect it rather than submitting again.");
    }

    public WorkClaim? Claim(string workbench, string claimId)
    {
        Assignment.Require(Guid.TryParseExact(claimId, "N", out _), "A claim ID in GUID N format is required.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request, claim, result, workbench FROM assignments
            WHERE (workbench = $workbench AND claim IS NOT NULL AND result IS NULL) OR claim = $claim;
            """;
        command.Parameters.AddWithValue("$workbench", workbench);
        command.Parameters.AddWithValue("$claim", claimId);
        using (var active = command.ExecuteReader())
        {
            if (active.Read())
            {
                if (active.GetString(1) != claimId || active.GetString(3) != workbench || !active.IsDBNull(2))
                    throw new DistributionConflictException("Workbench has unresolved work or this claim ID has already completed.");
                return new(claimId, Wire.Read<DispatchRequest>(active.GetString(0)));
            }
        }
        command.CommandText = """
            SELECT request FROM assignments WHERE workbench = $workbench AND claim IS NULL
            ORDER BY submitted, id LIMIT 1;
            """;
        if (command.ExecuteScalar() is not string json)
            return null;
        var dispatch = Wire.Read<DispatchRequest>(json);
        var claim = new WorkClaim(claimId, dispatch);
        command.CommandText = "UPDATE assignments SET claim = $claim, heartbeat = $heartbeat WHERE id = $id;";
        command.Parameters.AddWithValue("$heartbeat", Timestamp());
        command.Parameters.AddWithValue("$id", dispatch.Request.Assignment.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return claim;
    }

    public void Heartbeat(string id, string workbench, string claim)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE assignments SET heartbeat = $heartbeat
            WHERE id = $id AND workbench = $workbench AND claim = $claim AND result IS NULL;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workbench", workbench);
        command.Parameters.AddWithValue("$claim", claim);
        command.Parameters.AddWithValue("$heartbeat", Timestamp());
        if (command.ExecuteNonQuery() != 1)
            throw new DistributionConflictException("Claim is not active.");
    }

    public void Complete(string id, string workbench, string? claim, WorkerResult result)
    {
        ValidateResult(id, result);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request, claim, result FROM assignments WHERE id = $id AND workbench = $workbench;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workbench", workbench);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(1) || (claim is not null && reader.GetString(1) != claim))
            throw new DistributionConflictException("Assignment has no matching claim.");
        var request = Wire.Read<DispatchRequest>(reader.GetString(0)).Request;
        if (result.Status == "succeeded" && request.PublishPullRequest && string.IsNullOrWhiteSpace(result.PullRequestUrl))
            throw new InvalidDataException("Successful PR publication requires a PR URL.");
        var json = Wire.Write(result);
        if (!reader.IsDBNull(2))
        {
            if (reader.GetString(2) != json)
                throw new DistributionConflictException("A different terminal result is already recorded.");
            return;
        }
        reader.Close();
        command.CommandText = "UPDATE assignments SET result = $result WHERE id = $id;";
        command.Parameters.AddWithValue("$result", json);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public AssignmentState? Get(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT workbench, submitted, claim, heartbeat, result FROM assignments WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var heartbeat = reader.IsDBNull(3) ? (DateTimeOffset?)null : Parse(reader.GetString(3));
        var result = reader.IsDBNull(4) ? null : Wire.Read<WorkerResult>(reader.GetString(4));
        var status = result?.Status ?? (reader.IsDBNull(2) ? "queued"
            : clock.GetUtcNow() - heartbeat!.Value > HeartbeatLimit ? "attention" : "running");
        return new(id, reader.GetString(0), status, Parse(reader.GetString(1)), heartbeat, result);
    }

    public static void ValidateResult(string id, WorkerResult result)
    {
        Assignment.Require(result.AssignmentId == id, "Result assignment identity does not match.");
        Assignment.Require(result.Status is "succeeded" or "failed" or "cancelled", "Invalid terminal status.");
        Assignment.Require(result.Status == "succeeded" || !string.IsNullOrWhiteSpace(result.Error),
            "Failed or cancelled results need an explanation.");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private string Timestamp() => clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
