﻿namespace Proto.Cluster.Identity.MongoDb
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Identity;
    using Microsoft.Extensions.Logging;
    using MongoDB.Driver;

    public class MongoIdentityStorage : IIdentityStorage
    {
        private static readonly ILogger Logger = Log.CreateLogger<MongoIdentityStorage>();

        private readonly string _clusterName;
        private readonly IMongoCollection<PidLookupEntity> _pids;

        public MongoIdentityStorage(string clusterName, IMongoCollection<PidLookupEntity> pids)
        {
            _clusterName = clusterName;
            _pids = pids;
        }


        public async Task<SpawnLock?> TryAcquireLockAsync(
            ClusterIdentity clusterIdentity,
            CancellationToken ct)
        {
            var requestId = Guid.NewGuid().ToString();
            var hasLock = await TryAcquireLockAsync(clusterIdentity, requestId, ct);
            return hasLock ? new SpawnLock(requestId, clusterIdentity) : null;
        }

        public async Task<StoredActivation?> WaitForActivationAsync(ClusterIdentity clusterIdentity,
            CancellationToken ct)
        {
            var key = GetKey(clusterIdentity);
            var pidLookupEntity = await LookupKey(key, ct);
            var lockId = pidLookupEntity?.LockedBy;
            if (lockId != null)
            {
                //There is an active lock on the pid, spin wait
                var i = 0;
                do
                {
                    await Task.Delay(20 * i, ct);
                } while ((pidLookupEntity = await LookupKey(key, ct))?.LockedBy == lockId && ++i < 10);
            }

            //the lookup entity was lost, stale lock maybe?
            if (pidLookupEntity == null) return null;

            //lookup was unlocked, return this pid
            if (pidLookupEntity.LockedBy == null)
                return new StoredActivation(pidLookupEntity.MemberId,
                    PID.FromAddress(pidLookupEntity.Address, pidLookupEntity.UniqueIdentity)
                );

            //Still locked but not by the same request that originally locked it, so not stale
            if (pidLookupEntity.LockedBy != lockId) return null;

            //Stale lock. just delete it and let cluster retry
            // _logger.LogDebug($"Stale lock: {pidLookupEntity.Key}");
            await RemoveLock(new SpawnLock(lockId!, clusterIdentity), CancellationToken.None);
            return null;
        }

        public Task RemoveLock(SpawnLock spawnLock, CancellationToken ct)
            => _pids.DeleteManyAsync(p => p.LockedBy == spawnLock.LockId, ct);

        public async Task StoreActivation(string memberId, SpawnLock spawnLock, PID pid, CancellationToken ct)
        {
            Logger.LogDebug("Storing activation: {@ActivatorId}, {@SpawnLock}, {@PID}", memberId, spawnLock, pid);
            var key = GetKey(spawnLock.ClusterIdentity);
            var res = await _pids.UpdateOneAsync(
                s => s.Key == key && s.LockedBy == spawnLock.LockId && s.Revision == 1,
                Builders<PidLookupEntity>.Update
                    .Set(l => l.Address, pid.Address)
                    .Set(l => l.MemberId, memberId)
                    .Set(l => l.UniqueIdentity, pid.Id)
                    .Set(l => l.Revision, 2)
                    .Unset(l => l.LockedBy)
                , new UpdateOptions(), ct
            );
            if (res.MatchedCount != 1)
            {
                throw new StorageFailure($"Failed to store activation of {pid.ToShortString()}");
            }
        }

        public async Task RemoveActivation(PID pid, CancellationToken ct)
        {
            Logger.LogDebug("Removing activation: {@PID}", pid);

            await _pids.DeleteManyAsync(p => p.UniqueIdentity == pid.Id, ct);
        }

        public Task RemoveMemberIdAsync(string memberId, CancellationToken ct)
            => _pids.DeleteManyAsync(p => p.MemberId == memberId, ct);

        public async Task<StoredActivation?> TryGetExistingActivationAsync(ClusterIdentity clusterIdentity,
            CancellationToken ct)
        {
            var pidLookup = await LookupKey(GetKey(clusterIdentity), ct);
            return pidLookup?.Address == null || pidLookup?.UniqueIdentity == null
                ? null
                : new StoredActivation(pidLookup.MemberId,
                    PID.FromAddress(pidLookup.Address, pidLookup.UniqueIdentity)
                );
        }

        private async Task<bool> TryAcquireLockAsync(
            ClusterIdentity clusterIdentity,
            string requestId,
            CancellationToken ct)
        {
            var key = GetKey(clusterIdentity);
            var lockEntity = new PidLookupEntity
            {
                Address = null,
                Identity = clusterIdentity.Identity,
                Key = key,
                Kind = clusterIdentity.Kind,
                LockedBy = requestId,
                Revision = 1,
                MemberId = null
            };
            try
            {
                //be 100% sure own the lock here
                await _pids.InsertOneAsync(lockEntity, new InsertOneOptions(), ct);
                Logger.LogDebug("Got lock on first try for {ClusterIdentity}", clusterIdentity);
                return true;
            }
            catch (MongoWriteException)
            {
                var l = await _pids.ReplaceOneAsync(x => x.Key == key && x.LockedBy == null && x.Revision == 0,
                    lockEntity,
                    new ReplaceOptions
                    {
                        IsUpsert = false
                    }, ct
                );

                //if l.MatchCount == 1, then one document was updated by us, and we should own the lock, no?
                var gotLock =  l.IsAcknowledged && l.ModifiedCount == 1;
                Logger.LogDebug("Did {Got}get lock on second try for {ClusterIdentity}", gotLock ? "": "not ", clusterIdentity);
                return gotLock;
            }
        }

        private async Task<PidLookupEntity?> LookupKey(string key, CancellationToken ct)
        {

            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var res= await _pids.Find(x => x.Key == key).Limit(1).SingleOrDefaultAsync(ct);
                    return res;
                }
                catch(MongoConnectionException x)
                {
                    Logger.LogWarning(x,"Mongo connection failure, retrying");
                }

                // ReSharper disable once MethodSupportsCancellation
                await Task.Delay(i * 1000);
            }

            throw new StorageFailure($"Failed to connect to MongoDB while looking up key {key}");
        }

        private string GetKey(ClusterIdentity clusterIdentity) => $"{_clusterName}/{clusterIdentity.ToShortString()}";
        public void Dispose()
        {
        }
    }
}