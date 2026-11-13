using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace Factory.Plant.Service.Storage;

public sealed class CosmosRecordStore(Container container) : IRecordStore
{
    public async Task<StoredRecord?> ReadAsync(string scopeId, string id, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<StoreRecord>(id, new PartitionKey(scopeId),
                cancellationToken: cancellationToken);
            return new(response.Resource, response.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<StoreRecord>> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var sql = "SELECT TOP @limit * FROM c WHERE c.type = @type";
        if (query.PlantId is not null)
            sql += " AND c.plantId = @plant";
        if (query.Principal is not null)
            sql += " AND c.principal.tenantId = @tenant AND c.principal.objectId = @object";
        if (query.AfterId is not null)
            sql += " AND c.id > @after";
        sql += " ORDER BY c.id";
        var definition = new QueryDefinition(sql).WithParameter("@limit", query.Limit).WithParameter("@type", query.Type);
        if (query.PlantId is not null)
            definition.WithParameter("@plant", query.PlantId);
        if (query.Principal is not null)
            definition.WithParameter("@tenant", query.Principal.TenantId).WithParameter("@object", query.Principal.ObjectId);
        if (query.AfterId is not null)
            definition.WithParameter("@after", query.AfterId);

        using var iterator = container.GetItemQueryIterator<StoreRecord>(definition, requestOptions: new QueryRequestOptions
        {
            PartitionKey = query.ScopeId is null ? null : new PartitionKey(query.ScopeId),
            MaxItemCount = query.Limit,
            MaxConcurrency = 1
        });
        var records = new List<StoreRecord>();
        while (iterator.HasMoreResults)
            records.AddRange(await iterator.ReadNextAsync(cancellationToken));
        return records;
    }

    public async Task<bool> CommitAsync(string scopeId, IReadOnlyList<RecordWrite> writes, CancellationToken cancellationToken)
    {
        if (writes.Count == 0 || writes.Any(write => write.Record.ScopeId != scopeId))
            throw new InvalidOperationException("A transaction must contain records in exactly one partition.");

        var batch = container.CreateTransactionalBatch(new PartitionKey(scopeId));
        foreach (var write in writes)
        {
            if (write.ExpectedETag is null)
                batch.CreateItem<StoreRecord>(write.Record);
            else
                batch.ReplaceItem<StoreRecord>(write.Record.Id, write.Record,
                    new TransactionalBatchItemRequestOptions { IfMatchEtag = write.ExpectedETag });
        }
        using var response = await batch.ExecuteAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
            return true;
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            return false;
        throw new CosmosException("The record transaction failed.", response.StatusCode, 0,
            response.ActivityId, response.RequestCharge);
    }
}

public sealed class RecordSerializer : CosmosSerializer
{
    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(stream, Records.Json)
                    ?? throw new JsonException("The stored record was null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("The persisted record could not be decoded.", exception);
            }
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, input, Records.Json);
        stream.Position = 0;
        return stream;
    }
}
