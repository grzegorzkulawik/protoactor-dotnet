﻿using System.Threading;
using Proto.Router;

namespace Proto.Cluster.Identity
{
    internal class GetPid : IHashable
    {
        public GetPid(ClusterIdentity clusterIdentity, CancellationToken cancellationToken)
        {
            ClusterIdentity = clusterIdentity;
            CancellationToken = cancellationToken;
        }

        public ClusterIdentity ClusterIdentity { get; }
        public CancellationToken CancellationToken { get; }

        public string HashBy() => ClusterIdentity.ToShortString();
    }

    internal class PidResult
    {
        public PID? Pid { get; set; }
    }
}