﻿using System;
using Stratis.FederatedPeg.Features.FederationGateway.TargetChain;

namespace Stratis.FederatedPeg.Features.FederationGateway.Interfaces
{
    public interface ICrossChainDB : IDisposable
    {
        /// <summary>Initializes the cross-chain-transfer store.</summary>
        void Initialize();

        /// <summary>
        /// Creates a <see cref="CrossChainDBTransaction"/> for either read or read/write operations.
        /// </summary>
        /// <param name="mode">The mode which is either <see cref="CrossChainTransactionMode.Read"/>
        /// or <see cref="CrossChainTransactionMode.ReadWrite"/></param>
        /// <returns>The <see cref="CrossChainDBTransaction"/> object.</returns>
        CrossChainDBTransaction GetTransaction(CrossChainTransactionMode mode = CrossChainTransactionMode.Read);

        /// <summary>Updates the internal lookups based on the changes recorded in the tracker object.</summary>
        /// <param name="tracker">Information about how to update the lookups.</param>
        /// <remarks>This method should is only intended be called by the <see cref="CrossChainDBTransaction"/> class.</remarks>
        void UpdateLookups(StatusChangeTracker tracker);
    }
}
