using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Plant.Service.Storage;
using Xunit;

namespace Factory.Tests;

public sealed class StorageRecordTests
{
    [Fact]
    public void CosmosRecordSerializationPreservesTypedDataAndAcceptsSystemMetadataBeforeTheDiscriminator()
    {
        var id = Guid.NewGuid().ToString("D");
        StoreRecord record = new PlantRecord
        {
            Id = id,
            PlantId = id,
            ScopeId = Records.PlantScope(id),
            Revision = "\"revision\"",
            Plant = new("1", id, "Plant", new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId),
                "active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };
        var serializer = new RecordSerializer();
        using var encoded = serializer.ToStream(record);
        using var reader = new StreamReader(encoded);
        var json = reader.ReadToEnd();
        using var response = new MemoryStream(Encoding.UTF8.GetBytes("{\"_etag\":\"cosmos-revision\",\"_ts\":1," + json[1..]));
        var decoded = serializer.FromStream<StoreRecord>(response);
        Assert.Equal(record, Records.Require<PlantRecord>(decoded));
    }

    [Theory]
    [InlineData("{\"type\":\"unknown-record\"}")]
    [InlineData("{\"type\":\"plant\",\"id\":\"incomplete\"}")]
    [InlineData("null")]
    public void CorruptStoredDataIsAServerFailureNotAnInvalidClientRequest(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidOperationException>(() => new RecordSerializer().FromStream<StoreRecord>(stream));
    }
}
