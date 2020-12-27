﻿// -----------------------------------------------------------------------
//   <copyright file="Activator.cs" company="Asynkron AB">
//       Copyright (C) 2015-2020 Asynkron AB All rights reserved
//   </copyright>
// -----------------------------------------------------------------------

using Proto.Timers;

namespace Proto.Cluster.Identity
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;

    internal class IdentityStoragePlacementActor : IActor, IDisposable
    {
        private readonly Cluster _cluster;
        private readonly ILogger _logger;

        //pid -> the actor that we have created here
        //kind -> the actor kind
        //eventId -> the cluster wide eventId when this actor was created
        private readonly Dictionary<ClusterIdentity, PID> _myActors = new();

        private readonly IdentityStorageLookup _identityLookup;
        private CancellationTokenSource? _ct;

        public IdentityStoragePlacementActor(Cluster cluster, IdentityStorageLookup identityLookup)
        {
            _cluster = cluster;
            _identityLookup = identityLookup;
            _logger = Log.CreateLogger($"{nameof(IdentityStoragePlacementActor)}-{cluster.LoggerId}");
        }

        public Task ReceiveAsync(IContext context) => context.Message switch
        {
            Started _             => Started(context),
            Stopping _            => Stopping(context),
            Stopped _             => Stopped(context),
            Tick _                => Tick(context),
            Terminated msg        => Terminated(context, msg),
            ActivationRequest msg => ActivationRequest(context, msg),
            _                     => Task.CompletedTask
        };

        private Task Started(IContext context)
        {
            _ct = context.Scheduler().SendRepeatedly(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), context.Self!, new Tick());
            return Task.CompletedTask;
        }
        
        private Task Stopping(IContext context)
        {
            _logger.LogInformation("Stopping placement actor");
            return Task.CompletedTask;
        }
        
        private Task Stopped(IContext context)
        {
            _logger.LogInformation("Stopped placement actor");
            return Task.CompletedTask;
        }

        private Task Tick(IContext context)
        {
            var count = _myActors.Count;
            _logger.LogDebug("Statistics: Actor Count {ActorCount}", count);
            return Task.CompletedTask;
        }

        private async Task Terminated(IContext context, Terminated msg)
        {
            if (context.System.Shutdown.IsCancellationRequested)
            {
                return;
            }
            
            //TODO: if this turns out to be perf intensive, lets look at optimizations for reverse lookups
            var (identity, pid) = _myActors.FirstOrDefault(kvp => kvp.Value.Equals(msg.Who));
            _myActors.Remove(identity);
            _cluster.PidCache.RemoveByVal(identity, pid);
            await _identityLookup.RemovePidAsync(msg.Who,CancellationToken.None);
        }

        private Task ActivationRequest(IContext context, ActivationRequest msg)
        {
            var props = _cluster.GetClusterKind(msg.Kind);
            try
            {
                if (_myActors.TryGetValue(msg.ClusterIdentity, out var existing))
                {
                    //this identity already exists
                    var response = new ActivationResponse
                    {
                        Pid = existing
                    };
                    context.Respond(response);
                }
                else
                {
                    //this actor did not exist, lets spawn a new activation

                    //spawn and remember this actor
                    //as this id is unique for this activation (id+counter)
                    //we cannot get ProcessNameAlreadyExists exception here

                    var clusterProps = props.WithClusterInit(_cluster, msg.ClusterIdentity);
                    var pid = context.SpawnPrefix(clusterProps, msg.ClusterIdentity.ToString());

                    _myActors[msg.ClusterIdentity] = pid;
                    _cluster.PidCache.TryAdd(msg.ClusterIdentity, pid);

                    var response = new ActivationResponse
                    {
                        Pid = pid
                    };
                    context.Respond(response);

                    PersistActivation(context, msg, pid);
                }
            }
            catch
            {
                var response = new ActivationResponse
                {
                    Pid = null
                };
                context.Respond(response);
            }

            return Task.CompletedTask;
        }

        private void PersistActivation(IContext context, ActivationRequest msg, PID pid)
        {
            var spawnLock = new SpawnLock(msg.RequestId, msg.ClusterIdentity);
            try
            {
                _identityLookup.Storage.StoreActivation(_cluster.System.Id.ToString(), spawnLock, pid,
                    context.CancellationToken
                );
            }
            catch (Exception e)
            {
                //meaning, we spawned an actor but its placement is not stored anywhere
                _logger.LogCritical(e, "No entry was updated {@SpawnLock}", spawnLock);
            }
        }

        public void Dispose() => _ct?.Dispose();
    }
}