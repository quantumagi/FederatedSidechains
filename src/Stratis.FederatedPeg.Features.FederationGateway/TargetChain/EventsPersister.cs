﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stratis.FederatedPeg.Features.FederationGateway.Interfaces;

namespace Stratis.FederatedPeg.Features.FederationGateway.TargetChain
{
    public class EventsPersister : IEventPersister, IDisposable
    {
        private readonly ILogger logger;

        private readonly IDisposable maturedBlockDepositSubscription;

        private readonly ICrossChainTransferStore store;

        private readonly IMaturedBlocksRequester maturedBlocksRequester;

        public EventsPersister(ILoggerFactory loggerFactory,
                               ICrossChainTransferStore store,
                               IMaturedBlockReceiver maturedBlockReceiver,
                               IMaturedBlocksRequester maturedBlocksRequester)
        {
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.store = store;
            this.maturedBlocksRequester = maturedBlocksRequester;

            this.maturedBlockDepositSubscription = maturedBlockReceiver.MaturedBlockDepositStream.Subscribe(async m => await PersistNewMaturedBlockDepositsAsync(m).ConfigureAwait(false));
            this.logger.LogDebug("Subscribed to {0}", nameof(maturedBlockReceiver), nameof(maturedBlockReceiver.MaturedBlockDepositStream));
        }

        /// <inheritdoc />
        public async Task PersistNewMaturedBlockDepositsAsync(IMaturedBlockDeposits[] maturedBlockDeposits)
        {
            this.logger.LogDebug("New {0} received.", nameof(IMaturedBlockDeposits));

            if (maturedBlockDeposits?.Length <= 0)
                return;

            if (maturedBlockDeposits.Last().Block.BlockHeight < this.store.NextMatureDepositHeight)
                return;

            for (int i = 0; i < maturedBlockDeposits.Length; i++)
            {
                if (maturedBlockDeposits[i].Block.BlockHeight == this.store.NextMatureDepositHeight)
                {
                    await this.store.RecordLatestMatureDepositsAsync(maturedBlockDeposits[i].Deposits.ToArray()).ConfigureAwait(false);
                }
            }

            await this.maturedBlocksRequester.GetMoreBlocksAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task PersistNewSourceChainTip(IBlockTip newTip)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.store?.Dispose();
            this.maturedBlockDepositSubscription?.Dispose();
        }
    }
}
