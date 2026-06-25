using Application.ControlPlane;
using Domain.ControlPlane;
using Infrastructure.Persistence.MongoDb;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace Infrastructure.Services.MongoDb;

public class MongoLeaseStore : ILeaseStore
{
    public const string CollectionName = "dc_leases";

    private readonly IMongoCollection<DcLease> _collection;

    static MongoLeaseStore()
    {
        // The applied-watermark map is keyed by Guid. By default the driver serializes a
        // dictionary with non-string keys as an array of key/value pairs, which prevents
        // dotted-path $set updates like "appliedWatermarks.{envId}". Force the Document
        // representation so each environment id becomes a field name we can target directly.
        if (!BsonClassMap.IsClassMapRegistered(typeof(DcLease)))
        {
            BsonClassMap.RegisterClassMap<DcLease>(map =>
            {
                map.AutoMap();
                map.MapMember(x => x.AppliedWatermarks)
                    .SetSerializer(
                        new DictionaryInterfaceImplementerSerializer<Dictionary<Guid, long>>(
                            DictionaryRepresentation.Document,
                            new GuidSerializer(BsonType.String),
                            new Int64Serializer()));
            });
        }
    }

    public MongoLeaseStore(MongoDbClient mongoDb)
    {
        _collection = mongoDb.Database.GetCollection<DcLease>(CollectionName);
    }

    public async Task UpsertLeaseAsync(DcLease lease)
    {
        var filter = Builders<DcLease>.Filter.Eq(x => x.DcId, lease.DcId);
        await _collection.ReplaceOneAsync(filter, lease, new ReplaceOptions { IsUpsert = true });
    }

    public async Task<IReadOnlyList<DcLease>> GetLiveSetAsync(DateTimeOffset now)
    {
        var filter = Builders<DcLease>.Filter.Gt(x => x.LeaseExpiresAt, now);
        var leases = await _collection.Find(filter).ToListAsync();
        return leases;
    }

    public async Task UpdateAppliedWatermarkAsync(string dcId, Guid envId, long version)
    {
        var filter = Builders<DcLease>.Filter.Eq(x => x.DcId, dcId);
        var update = Builders<DcLease>.Update.Set($"appliedWatermarks.{envId}", version);
        await _collection.UpdateOneAsync(filter, update);
    }
}
