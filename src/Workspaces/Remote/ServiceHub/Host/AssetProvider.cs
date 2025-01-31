﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Serialization;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Remote;

/// <summary>
/// This service provide a way to get roslyn objects from checksum
/// </summary>
internal sealed partial class AssetProvider(Checksum solutionChecksum, SolutionAssetCache assetCache, IAssetSource assetSource, ISerializerService serializerService)
    : AbstractAssetProvider
{
    private const int PooledChecksumArraySize = 256;
    private static readonly ObjectPool<Checksum[]> s_checksumPool = new(() => new Checksum[PooledChecksumArraySize], 16);

    private readonly Checksum _solutionChecksum = solutionChecksum;
    private readonly ISerializerService _serializerService = serializerService;
    private readonly SolutionAssetCache _assetCache = assetCache;
    private readonly IAssetSource _assetSource = assetSource;

    private T GetRequiredAsset<T>(Checksum checksum)
    {
        Contract.ThrowIfTrue(checksum == Checksum.Null);
        Contract.ThrowIfFalse(_assetCache.TryGetAsset<T>(checksum, out var asset));
        return asset;
    }

    public override async ValueTask<T> GetAssetAsync<T>(
        AssetHint assetHint, Checksum checksum, CancellationToken cancellationToken)
    {
        Contract.ThrowIfTrue(checksum == Checksum.Null);
        if (_assetCache.TryGetAsset<T>(checksum, out var asset))
            return asset;

        using var _ = PooledHashSet<Checksum>.GetInstance(out var checksums);
        checksums.Add(checksum);

        await this.SynchronizeAssetsAsync(assetHint, checksums, cancellationToken).ConfigureAwait(false);

        return GetRequiredAsset<T>(checksum);
    }

    public async ValueTask<ImmutableArray<ValueTuple<Checksum, T>>> GetAssetsAsync<T>(
        AssetHint assetHint, HashSet<Checksum> checksums, CancellationToken cancellationToken)
    {
        // bulk synchronize checksums first
        var syncer = new ChecksumSynchronizer(this);
        await syncer.SynchronizeAssetsAsync(assetHint, checksums, cancellationToken).ConfigureAwait(false);

        // this will be fast since we actually synchronized the checksums above.
        using var _ = ArrayBuilder<ValueTuple<Checksum, T>>.GetInstance(checksums.Count, out var list);
        foreach (var checksum in checksums)
            list.Add(ValueTuple.Create(checksum, GetRequiredAsset<T>(checksum)));

        return list.ToImmutableAndClear();
    }

    public async ValueTask SynchronizeSolutionAssetsAsync(Checksum solutionChecksum, CancellationToken cancellationToken)
    {
        var timer = new Stopwatch();
        timer.Start();

        // this will pull in assets that belong to the given solution checksum to this remote host.
        // this one is not supposed to be used for functionality but only for perf. that is why it doesn't return anything.
        // to get actual data GetAssetAsync should be used. and that will return actual data and if there is any missing data in cache, GetAssetAsync
        // itself will bring that data in from data source (VS)

        // one can call this method to make cache hot for all assets that belong to the solution checksum so that GetAssetAsync call will most likely cache hit.
        // it is most likely since we might change cache hueristic in future which make data to live a lot shorter in the cache, and the data might get expired
        // before one actually consume the data. 
        using (Logger.LogBlock(FunctionId.AssetService_SynchronizeSolutionAssetsAsync, Checksum.GetChecksumLogInfo, solutionChecksum, cancellationToken))
        {
            var syncer = new ChecksumSynchronizer(this);
            await syncer.SynchronizeSolutionAssetsAsync(solutionChecksum, cancellationToken).ConfigureAwait(false);
        }

        timer.Stop();

        // report telemetry to help correlate slow solution sync with UI delays
        if (timer.ElapsedMilliseconds > 1000)
        {
            Logger.Log(FunctionId.AssetService_Perf, KeyValueLogMessage.Create(map => map["SolutionSyncTime"] = timer.ElapsedMilliseconds));
        }
    }

    public async ValueTask SynchronizeProjectAssetsAsync(ProjectStateChecksums projectChecksums, CancellationToken cancellationToken)
    {
        // this will pull in assets that belong to the given project checksum to this remote host. this one is not
        // supposed to be used for functionality but only for perf. that is why it doesn't return anything. to get
        // actual data GetAssetAsync should be used. and that will return actual data and if there is any missing data
        // in cache, GetAssetAsync itself will bring that data in from data source (VS)

        // one can call this method to make cache hot for all assets that belong to the project checksum so that
        // GetAssetAsync call will most likely cache hit. it is most likely since we might change cache heuristic in
        // future which make data to live a lot shorter in the cache, and the data might get expired before one actually
        // consume the data. 
        using (Logger.LogBlock(FunctionId.AssetService_SynchronizeProjectAssetsAsync, Checksum.GetProjectChecksumsLogInfo, projectChecksums, cancellationToken))
        {
            var syncer = new ChecksumSynchronizer(this);
            await syncer.SynchronizeProjectAssetsAsync(projectChecksums, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask SynchronizeAssetsAsync(
        AssetHint assetHint, HashSet<Checksum> checksums, CancellationToken cancellationToken)
    {
        Contract.ThrowIfTrue(checksums.Contains(Checksum.Null));
        if (checksums.Count == 0)
            return;

        using (Logger.LogBlock(FunctionId.AssetService_SynchronizeAssetsAsync, Checksum.GetChecksumsLogInfo, checksums, cancellationToken))
        {
            var missingChecksumsCount = 0;

            // Calculate the number of missing checksums upfront. Calculation is cheap and can help avoid extraneous allocations.
            foreach (var checksum in checksums)
            {
                if (!_assetCache.ContainsAsset(checksum))
                    missingChecksumsCount++;
            }

            var usePool = missingChecksumsCount <= PooledChecksumArraySize;
            var missingChecksums = usePool ? s_checksumPool.Allocate() : new Checksum[missingChecksumsCount];

            missingChecksumsCount = 0;
            foreach (var checksum in checksums)
            {
                if (!_assetCache.TryGetAsset<object>(checksum, out _))
                {
                    if (missingChecksumsCount == missingChecksums.Length)
                    {
                        // This can happen if the asset cache has been modified by another thread during this method's execution.
                        var newMissingChecksums = new Checksum[missingChecksumsCount * 2];
                        Array.Copy(missingChecksums, newMissingChecksums, missingChecksumsCount);

                        if (usePool)
                        {
                            s_checksumPool.Free(missingChecksums);
                            usePool = false;
                        }

                        missingChecksums = newMissingChecksums;
                    }

                    missingChecksums[missingChecksumsCount] = checksum;
                    missingChecksumsCount++;
                }
            }

            var missingChecksumsMemory = new ReadOnlyMemory<Checksum>(missingChecksums, 0, missingChecksumsCount);
            var assets = await RequestAssetsAsync(assetHint, missingChecksumsMemory, cancellationToken).ConfigureAwait(false);

            Contract.ThrowIfTrue(missingChecksumsMemory.Length != assets.Length);

            for (int i = 0, n = assets.Length; i < n; i++)
                _assetCache.GetOrAdd(missingChecksums[i], assets[i]);

            if (usePool)
                s_checksumPool.Free(missingChecksums);
        }
    }

    private async Task<ImmutableArray<object>> RequestAssetsAsync(
        AssetHint assetHint, ReadOnlyMemory<Checksum> checksums, CancellationToken cancellationToken)
    {
#if NETCOREAPP
        Contract.ThrowIfTrue(checksums.Span.Contains(Checksum.Null));
#else
        Contract.ThrowIfTrue(checksums.Span.IndexOf(Checksum.Null) >= 0);
#endif

        if (checksums.Length == 0)
            return [];

        return await _assetSource.GetAssetsAsync(_solutionChecksum, assetHint, checksums, _serializerService, cancellationToken).ConfigureAwait(false);
    }
}
