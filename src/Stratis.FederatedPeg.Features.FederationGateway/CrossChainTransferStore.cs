﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DBreeze;
using DBreeze.DataTypes;
using DBreeze.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Utilities;
using Stratis.FederatedPeg.Features.FederationGateway.Interfaces;
using Stratis.FederatedPeg.Features.FederationGateway.SourceChain;

namespace Stratis.FederatedPeg.Features.FederationGateway
{
    /// <summary>
    /// Interface for interacting with the cross-chain transfer database.
    /// </summary>
    public interface ICrossChainTransferStore : IDisposable
    {
        /// <summary>
        /// Get the cross-chain transfer information from the database, identified by the deposit transaction ids.
        /// </summary>
        /// <param name="depositIds">The deposit transaction ids.</param>
        /// <returns>The cross-chain transfer information.</returns>
        Task<CrossChainTransfer[]> GetAsync(uint256[] depositIds);

        /// <summary>
        /// Records the mature deposits at <see cref="NextMatureDepositHeight"/> on the counter-chain.
        /// The value of <see cref="NextMatureDepositHeight"/> is incremented at the end of this call.
        /// </summary>
        /// <param name="crossChainTransfers">The deposit transactions.</param>
        /// <remarks>
        /// When building the list of transfers the caller should first use <see cref="GetAsync"/>
        /// to check whether the transfer already exists without the deposit information and
        /// then provide the updated object in this call.
        /// The caller must also ensure the transfers passed to this call all have a
        /// <see cref="CrossChainTransfer.status"/> of <see cref="CrossChainTransferStatus.Partial"/>.
        /// </remarks>
        Task RecordLatestMatureDeposits(IEnumerable<CrossChainTransfer> crossChainTransfers);

        /// <summary>
        /// Uses the information contained in our chain's blocks to update the store.
        /// Sets the <see cref="CrossChainTransferStatus.SeenInBlock"/> status for transfers
        /// identified in the blocks.
        /// </summary>
        /// <param name="newTip">The new <see cref="ChainTip"/>.</param>
        /// <param name="blocks">The blocks used to update the store. Must be sorted by ascending height leading up to the new tip.</param>
        Task PutAsync(HashHeightPair newTip, List<Block> blocks);

        /// <summary>
        /// Used in case of a reorg to revert status from <see cref="CrossChainTransferStatus.SeenInBlock"/> to
        /// <see cref="CrossChainTransferStatus.FullySigned"/>. This is expected to trigger the re-broadcasting
        /// of the transaction.
        /// </summary>
        /// <param name="newTip">The new <see cref="ChainTip"/>.</param>
        Task DeleteAsync(BlockLocator newTip);

        /// <summary>
        /// Updates partial transactions in the store with signatures obtained from the passed transactions.
        /// </summary>
        /// <param name="partialTransactions">Partial transactions received from other federation members.</param>
        /// <remarks>
        /// The following statuses may be set:
        /// <list type="bullet">
        /// <item><see cref="CrossChainTransferStatus.FullySigned"/></item>
        /// </list>
        /// </remarks>
        Task MergeTransactionSignatures(Transaction[] partialTransactions);

        /// <summary>
        /// The tip of our chain when we last updated the store.
        /// </summary>
        HashHeightPair TipHashAndHeight { get; }

        /// <summary>
        /// The block height on the counter-chain for which the next list of deposits is expected.
        /// </summary>
        long NextMatureDepositHeight { get; }
    }

    public class CrossChainTransferStore : ICrossChainTransferStore
    {
        /// <summary>This table contains the cross-chain transfer information.</summary>
        private const string transferTableName = "Transfers";

        /// <summary>This table keeps track of the chain tips so that we know exactly what data our transfer table contains.</summary>
        private const string commonTableName = "Common";

        /// <summary>This contains deposits ids indexed by block hash of the corresponding transaction.</summary>
        private Dictionary<uint256, HashSet<uint256>> depositIdsByBlockHash = new Dictionary<uint256, HashSet<uint256>>();

        /// <summary>This contains the block heights by block hashes for only the blocks of interest in our chain.</summary>
        private Dictionary<uint256, int> blockHeightsByBlockHash = new Dictionary<uint256, int>();

        /// <summary>This table contains deposits ids by status.</summary>
        private Dictionary<CrossChainTransferStatus, HashSet<uint256>> depositsByStatus = new Dictionary<CrossChainTransferStatus, HashSet<uint256>>();

        /// <inheritdoc />
        public long NextMatureDepositHeight { get; private set; }

        /// <inheritdoc />
        public HashHeightPair TipHashAndHeight { get; private set; }

        /// <summary>The key of the repository tip in the common table.</summary>
        private static readonly byte[] RepositoryTipKey = new byte[0];

        /// <summary>Instance logger.</summary>
        private readonly ILogger logger;

        /// <summary>Access to DBreeze database.</summary>
        private readonly DBreezeEngine DBreeze;

        private readonly Network network;

        private readonly DepositExtractor depositExtractor;

        /// <summary>Provider of time functions.</summary>
        private readonly IDateTimeProvider dateTimeProvider;

        public CrossChainTransferStore(Network network, DataFolder dataFolder, FederationGatewaySettings settings, IDateTimeProvider dateTimeProvider,
            ILoggerFactory loggerFactory, IOpReturnDataReader opReturnDataReader)
        {
            Guard.NotNull(network, nameof(network));
            Guard.NotNull(dataFolder, nameof(dataFolder));
            Guard.NotNull(settings, nameof(settings));
            Guard.NotNull(dateTimeProvider, nameof(dateTimeProvider));
            Guard.NotNull(loggerFactory, nameof(loggerFactory));

            this.depositExtractor = new DepositExtractor(loggerFactory, settings, opReturnDataReader);
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);

            string folder = Path.Combine(dataFolder.RootPath, settings.IsMainChain ? "mainchaindata" : "sidechaindata");
            Directory.CreateDirectory(folder);
            this.DBreeze = new DBreezeEngine(folder);
            this.network = network;
            this.dateTimeProvider = dateTimeProvider;
            this.NextMatureDepositHeight = 0;

            // Initialize tracking deposits by status.
            foreach (var status in typeof(CrossChainTransferStatus).GetEnumValues())
                this.depositsByStatus[(CrossChainTransferStatus)status] = new HashSet<uint256>();
        }

        /// <summary>Performs any needed initialisation for the database.</summary>
        public virtual Task InitializeAsync()
        {
            Task task = Task.Run(() =>
            {
                this.logger.LogTrace("()");

                using (DBreeze.Transactions.Transaction dbreezeTransaction = this.DBreeze.GetTransaction())
                {
                    this.LoadTipHashAndHeight(dbreezeTransaction);

                    // Initialize the lookups.
                    foreach (Row<byte[], CrossChainTransfer> transferRow in dbreezeTransaction.SelectForward<byte[], CrossChainTransfer>(transferTableName))
                    {
                        CrossChainTransfer transfer = transferRow.Value;
                        if (transfer.BlockHash != null)
                        {
                            if (!this.depositIdsByBlockHash.TryGetValue(transfer.BlockHash, out HashSet<uint256> deposits))
                            {
                                deposits = new HashSet<uint256>();
                            }

                            deposits.Add(transfer.DepositTransactionId);

                            this.depositsByStatus[transfer.Status].Add(transfer.DepositTransactionId);

                            this.blockHeightsByBlockHash[transfer.BlockHash] = transfer.BlockHeight;
                        }
                    }
                }

                this.logger.LogTrace("(-)");
            });

            return task;
        }

        /// <inheritdoc />
        public Task RecordLatestMatureDeposits(IEnumerable<CrossChainTransfer> crossChainTransfers)
        {
            throw new NotImplementedException("Not implemented yet");
        }

        /// <inheritdoc />
        public Task MergeTransactionSignatures(Transaction[] partialTransactions)
        {
            throw new NotImplementedException("Not implemented yet");
        }

        /// <inheritdoc />
        public Task PutAsync(HashHeightPair newTip, List<Block> blocks)
        {
            Guard.NotNull(newTip, nameof(newTip));
            Guard.NotNull(blocks, nameof(blocks));

            Task task = Task.Run(() =>
            {
                using (DBreeze.Transactions.Transaction transaction = this.DBreeze.GetTransaction())
                {
                    transaction.SynchronizeTables(transferTableName, commonTableName);
                    this.OnInsertBlocks(transaction, newTip.Height - blocks.Count + 1, blocks);

                    // Commit additions
                    this.SaveTipHashAndHeight(transaction, newTip);
                    transaction.Commit();
                }
            });

            return task;
        }

        /// <inheritdoc />
        public Task DeleteAsync(BlockLocator newTip)
        {
            Guard.NotNull(newTip, nameof(newTip));

            Task task = Task.Run(() =>
            {
                using (DBreeze.Transactions.Transaction transaction = this.DBreeze.GetTransaction())
                {
                    transaction.SynchronizeTables(transferTableName, commonTableName);
                    transaction.ValuesLazyLoadingIsOn = false;

                    uint256 commonTip = null;

                    foreach (uint256 hash in newTip.Blocks)
                    {
                        if (this.depositIdsByBlockHash.ContainsKey(hash))
                        {
                            commonTip = hash;
                            break;
                        }
                    }

                    int commonHeight = this.blockHeightsByBlockHash[commonTip];

                    this.OnDeleteBlocks(transaction, commonHeight);
                    this.SaveTipHashAndHeight(transaction, new HashHeightPair(commonTip, commonHeight));
                    transaction.Commit();
                }
            });

            return task;
        }

        /// <summary>
        /// Loads the tip and hash height.
        /// </summary>
        /// <param name="dbreezeTransaction">The DBreeze transaction context to use.</param>
        /// <returns>The hash and height pair.</returns>
        private HashHeightPair LoadTipHashAndHeight(DBreeze.Transactions.Transaction dbreezeTransaction)
        {
            if (this.TipHashAndHeight == null)
            {
                dbreezeTransaction.ValuesLazyLoadingIsOn = false;

                Row<byte[], HashHeightPair> row = dbreezeTransaction.Select<byte[], HashHeightPair>(commonTableName, RepositoryTipKey);
                if (row.Exists)
                    this.TipHashAndHeight = row.Value;

                dbreezeTransaction.ValuesLazyLoadingIsOn = true;
            }

            return this.TipHashAndHeight;
        }

        /// <summary>
        /// Saves the tip and hash height.
        /// </summary>
        /// <param name="dbreezeTransaction">The DBreeze transaction context to use.</param>
        /// <param name="newTip">The new tip to persist.</param>
        private void SaveTipHashAndHeight(DBreeze.Transactions.Transaction dbreezeTransaction, HashHeightPair newTip)
        {
            this.TipHashAndHeight = newTip;
            dbreezeTransaction.Insert<byte[], HashHeightPair>(commonTableName, RepositoryTipKey, this.TipHashAndHeight);
        }

        /// <inheritdoc />
        public Task<CrossChainTransfer[]> GetAsync(uint256[] depositId)
        {
            Guard.NotNull(depositId, nameof(depositId));

            Task<CrossChainTransfer[]> task = Task.Run(() =>
            {
                this.logger.LogTrace("()");

                // To boost performance we will access the deposits sorted by deposit id.
                var depositDict = new Dictionary<uint256, int>();
                for (int i = 0; i < depositId.Length; i++)
                    depositDict[depositId[i]] = i;

                var byteListComparer = new ByteListComparer();
                List<KeyValuePair<uint256, int>> depositList = depositDict.ToList();
                depositList.Sort((pair1, pair2) => byteListComparer.Compare(pair1.Key.ToBytes(), pair2.Key.ToBytes()));

                var res = new CrossChainTransfer[depositId.Length];
                using (DBreeze.Transactions.Transaction transaction = this.DBreeze.GetTransaction())
                {
                    transaction.ValuesLazyLoadingIsOn = false;

                    foreach (KeyValuePair<uint256, int> kv in depositList)
                    {
                        Row<byte[], CrossChainTransfer> transferRow = transaction.Select<byte[], CrossChainTransfer>(transferTableName, kv.Key.ToBytes());

                        if (transferRow.Exists)
                        {
                            res[kv.Value] = transferRow.Value;
                        }
                    }
                }

                this.logger.LogTrace("(-):{0}", res);
                return res;
            });

            return task;
        }

        /// <summary>
        /// Persist the cross-chain transfer information into the database.
        /// </summary>
        /// <param name="dbreezeTransaction">The DBreeze transaction context to use.</param>
        /// <param name="crossChainTransfer">Cross-chain transfer information to be inserted.</param>
        private Task PutTransferAsync(DBreeze.Transactions.Transaction dbreezeTransaction, CrossChainTransfer crossChainTransfer)
        {
            Guard.NotNull(crossChainTransfer, nameof(crossChainTransfer));

            Task task = Task.Run(() =>
            {
                this.logger.LogTrace("()");

                dbreezeTransaction.Insert<byte[], CrossChainTransfer>(transferTableName, crossChainTransfer.DepositTransactionId.ToBytes(), crossChainTransfer);

                this.logger.LogTrace("(-)");
            });

            return task;
        }

        /// <summary>
        /// Records transfer information from the supplied blocks.
        /// </summary>
        /// <param name="dbreezeTransaction">The DBreeze transaction context to use.</param>
        /// <param name="blockHeight">The block height of the first block in the list.</param>
        /// <param name="blocks">The list of blocks to add.</param>
        private void OnInsertBlocks(DBreeze.Transactions.Transaction dbreezeTransaction, int blockHeight, List<Block> blocks)
        {
            // Find transfer transactions in blocks
            foreach (Block block in blocks)
            {
                IReadOnlyList<IDeposit> deposits = this.depositExtractor.ExtractDepositsFromBlock(block, blockHeight);

                // First check the database to see if we already know about these deposits.
                CrossChainTransfer[] storedDeposits = this.GetAsync(deposits.Select(d => d.Id).ToArray()).GetAwaiter().GetResult();

                // Update the information about these deposits or record their status.
                for (int i = 0; i < storedDeposits.Length; i++)
                {
                    IDeposit deposit = deposits[i];

                    if (storedDeposits[i] == null)
                    {
                        Script scriptPubKey = BitcoinAddress.Create(deposit.TargetAddress, this.network).ScriptPubKey;
                        Transaction transaction = block.Transactions.Single(t => t.GetHash() == deposit.Id);

                        storedDeposits[i] = new CrossChainTransfer(CrossChainTransferStatus.SeenInBlock, deposit.Id, deposit.BlockNumber,
                            scriptPubKey, deposit.Amount, transaction, block.GetHash(), blockHeight);

                        // Update the lookups.
                        this.depositsByStatus[CrossChainTransferStatus.SeenInBlock].Add(storedDeposits[i].DepositTransactionId);
                        this.depositIdsByBlockHash[block.GetHash()].Add(deposit.Id);
                    }
                    else
                    {
                        // Update the lookups.
                        this.SetTransferStatus(storedDeposits[i], CrossChainTransferStatus.SeenInBlock);
                    }

                    this.PutTransferAsync(dbreezeTransaction, storedDeposits[i]);
                }

                // Update lookups.
                this.blockHeightsByBlockHash[block.GetHash()] = blockHeight++;
            }
        }

        /// <summary>
        /// Forgets transfer information from the blocks being removed.
        /// </summary>
        /// <param name="dbreezeTransaction">The DBreeze transaction context to use.</param>
        /// <param name="lastBlockHeight">The last block to retain.</param>
        private void OnDeleteBlocks(DBreeze.Transactions.Transaction dbreezeTransaction, int lastBlockHeight)
        {
            // Gather all the deposit ids.
            var depositIds = new HashSet<uint256>();
            uint256[] blocksToRemove = this.blockHeightsByBlockHash.Where(a => a.Value > lastBlockHeight).Select(a => a.Key).ToArray();

            foreach (HashSet<uint256> deposits in blocksToRemove.Select(a => this.depositIdsByBlockHash[a]))
            {
                depositIds.UnionWith(deposits);
            }

            foreach (KeyValuePair<uint256, HashSet<uint256>> kv in this.depositIdsByBlockHash)
            {
                int blockHeight = this.blockHeightsByBlockHash[kv.Key];
                if (blockHeight > lastBlockHeight)
                {
                    depositIds.UnionWith(kv.Value);
                }
            }

            // First check the database to see if we already know about these deposits.
            CrossChainTransfer[] crossChainTransfers = this.GetAsync(depositIds.ToArray()).GetAwaiter().GetResult();

            foreach (CrossChainTransfer transfer in crossChainTransfers)
            {
                // Transaction is no longer seen.
                this.SetTransferStatus(transfer, CrossChainTransferStatus.FullySigned);

                // Update the lookups.
                this.depositIdsByBlockHash[transfer.BlockHash].Remove(transfer.DepositTransactionId);
            }

            // Update the lookups.
            foreach (uint256 blockHash in blocksToRemove)
            {
                this.blockHeightsByBlockHash.Remove(blockHash);
            }
        }

        private void SetTransferStatus(CrossChainTransfer transfer, CrossChainTransferStatus status)
        {
            this.depositsByStatus[transfer.Status].Remove(transfer.DepositTransactionId);
            transfer.SetStatus(status);
            this.depositsByStatus[transfer.Status].Add(transfer.DepositTransactionId);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.DBreeze.Dispose();
        }
    }
}
