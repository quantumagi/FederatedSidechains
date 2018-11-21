﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Connection;
using Stratis.Bitcoin.P2P.Peer;
using Stratis.Bitcoin.Utilities;
using Stratis.FederatedPeg.Features.FederationGateway.Interfaces;
using Stratis.FederatedPeg.Features.FederationGateway.NetworkHelpers;

namespace Stratis.FederatedPeg.Features.FederationGateway.TargetChain
{
    public class PartialTransactionRequester : IPartialTransactionRequester
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;
        private readonly ICrossChainTransferStore crossChainTransferStore;
        private readonly IAsyncLoopFactory asyncLoopFactory;
        private readonly INodeLifetime nodeLifetime;
        private readonly IConnectionManager connectionManager;
        private readonly IFederationGatewaySettings federationGatewaySettings;

        private IAsyncLoop asyncLoop;

        public PartialTransactionRequester(
            ILoggerFactory loggerFactory,
            ICrossChainTransferStore crossChainTransferStore,
            IAsyncLoopFactory asyncLoopFactory,
            INodeLifetime nodeLifetime,
            IConnectionManager connectionManager,
            IFederationGatewaySettings federationGatewaySettings)
        {
            Guard.NotNull(loggerFactory, nameof(loggerFactory));
            Guard.NotNull(crossChainTransferStore, nameof(crossChainTransferStore));
            Guard.NotNull(asyncLoopFactory, nameof(asyncLoopFactory));
            Guard.NotNull(nodeLifetime, nameof(nodeLifetime));
            Guard.NotNull(federationGatewaySettings, nameof(federationGatewaySettings));

            this.loggerFactory = loggerFactory;
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.crossChainTransferStore = crossChainTransferStore;
            this.asyncLoopFactory = asyncLoopFactory;
            this.nodeLifetime = nodeLifetime;
            this.connectionManager = connectionManager;
            this.federationGatewaySettings = federationGatewaySettings;
        }

        /// <summary>
        /// Broadcast the partial transaction request to federation members.
        /// </summary>
        /// <param name="payload">The payload to broadcast.</param>
        async Task BroadcastAsync(RequestPartialTransactionPayload payload)
        {
            this.logger.LogTrace("({0}:'{1}',{2}:'{3}')", nameof(payload.Command), payload.Command, nameof(payload.DepositId), payload.DepositId);

            List<INetworkPeer> peers = this.connectionManager.ConnectedPeers.ToList();

            var ipAddressComparer = new IPAddressComparer();

            foreach (INetworkPeer peer in peers)
            {
                if (this.federationGatewaySettings.FederationNodeIpEndPoints.Any(e => ipAddressComparer.Equals(e.Address, peer.PeerEndPoint.Address)))
                {
                    try
                    {
                        await peer.SendMessageAsync(payload).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }

            this.logger.LogTrace("(-)");
        }

        public void Start()
        {
            this.asyncLoop = this.asyncLoopFactory.Run("Get partial templates job", token =>
            {
                Dictionary<uint256, Transaction> transactions = this.crossChainTransferStore.GetTransactionsByStatusAsync(
                    CrossChainTransferStatus.Partial).GetAwaiter().GetResult();

                foreach (KeyValuePair<uint256, Transaction> kv in transactions)
                {
                    BroadcastAsync(new RequestPartialTransactionPayload(kv.Key).AddPartial(kv.Value)).GetAwaiter().GetResult();
                }

                if (transactions.Count > 0)
                {
                    this.logger.LogInformation("Partial templates requested");
                }

                this.logger.LogTrace("(-)[PARTIAL_TEMPLATES_JOB]");
                return Task.CompletedTask;
            }, this.nodeLifetime.ApplicationStopping, repeatEvery: TimeSpans.TenSeconds, startAfter: TimeSpans.TenSeconds);
        }

        public void Stop()
        {
            if (this.asyncLoop != null)
            {
                this.asyncLoop.Dispose();
                this.asyncLoop = null;
            }
        }
    }
}