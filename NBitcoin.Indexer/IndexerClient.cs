﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using NBitcoin.DataEncoders;
using NBitcoin.OpenAsset;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace NBitcoin.Indexer
{
    public class IndexerClient
    {
        private readonly IndexerConfiguration _Configuration;
        public IndexerConfiguration Configuration
        {
            get
            {
                return _Configuration;
            }
        }

        public IndexerClient(IndexerConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException("configuration");
            _Configuration = configuration;
            AddWalletRuleTypeConverter<AddressRule>();

        }


        public Block GetBlock(uint256 blockId)
        {
            var ms = new MemoryStream();
            var container = Configuration.GetBlocksContainer();
            try
            {

                container.GetPageBlobReference(blockId.ToString()).DownloadToStream(ms);
                ms.Position = 0;
                Block b = new Block();
                b.ReadWrite(ms, false);
                return b;
            }
            catch (StorageException ex)
            {
                if (ex.RequestInformation != null && ex.RequestInformation.HttpStatusCode == 404)
                {
                    return null;
                }
                throw;
            }
        }

        public TransactionEntry GetTransaction(bool lazyLoadSpentOutput, uint256 txId)
        {
            return GetTransactions(lazyLoadSpentOutput, new uint256[] { txId }).First();
        }
        public TransactionEntry GetTransaction(uint256 txId)
        {
            return GetTransactions(true, new[] { txId }).First();
        }

        public TransactionEntry[] GetTransactions(bool lazyLoadPreviousOutput, uint256[] txIds)
        {
            return GetTransactions(lazyLoadPreviousOutput, false, txIds);
        }

        public TransactionEntry GetTransaction(bool lazyLoadPreviousOutput, bool fetchColor, uint256 txId)
        {
            return GetTransactions(lazyLoadPreviousOutput, fetchColor, new[] { txId }).First();
        }

        /// <summary>
        /// Get transactions in Azure Table
        /// </summary>
        /// <param name="txIds"></param>
        /// <returns>All transactions (with null entries for unfound transactions)</returns>
        public TransactionEntry[] GetTransactions(bool lazyLoadPreviousOutput, bool fetchColor, uint256[] txIds)
        {
            var result = new TransactionEntry[txIds.Length];
            var queries = new TableQuery[txIds.Length];
            try
            {
                Parallel.For(0, txIds.Length, i =>
                {
                    var table = Configuration.GetTransactionTable();
                    var searchedEntity = new TransactionEntry.Entity(txIds[i]);
                    queries[i] = new TableQuery()
                                    .Where(
                                            TableQuery.CombineFilters(
                                                TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, searchedEntity.PartitionKey),
                                                TableOperators.And,
                                                TableQuery.CombineFilters(
                                                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.GreaterThan, txIds[i].ToString() + "-"),
                                                    TableOperators.And,
                                                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.LessThan, txIds[i].ToString() + "|")
                                                )
                                          ));

                    var entities = table.ExecuteQuery(queries[i])
                                       .Select(e => new TransactionEntry.Entity(e)).ToArray();
                    if (entities.Length == 0)
                        result[i] = null;
                    else
                    {
                        result[i] = new TransactionEntry(entities);
                        if (result[i].Transaction == null)
                        {
                            foreach (var block in result[i].BlockIds.Select(id => GetBlock(id)).Where(b => b != null))
                            {
                                result[i].Transaction = block.Transactions.FirstOrDefault(t => t.GetHash() == txIds[i]);
                                entities[0].Transaction = result[i].Transaction;
                                if (entities[0].Transaction != null)
                                {
                                    table.Execute(TableOperation.Merge(entities[0].CreateTableEntity()));
                                }
                                break;
                            }
                        }

                        if (fetchColor && result[i].ColoredTransaction == null)
                        {
                            result[i].ColoredTransaction = ColoredTransaction.FetchColors(txIds[i], result[i].Transaction, new IndexerColoredTransactionRepository(Configuration.AsServer()));
                            entities[0].ColoredTransaction = result[i].ColoredTransaction;
                            if (entities[0].ColoredTransaction != null)
                            {
                                table.Execute(TableOperation.Merge(entities[0].CreateTableEntity()));
                            }
                        }

                        var needTxOut = result[i].SpentCoins == null && lazyLoadPreviousOutput && result[i].Transaction != null;
                        if (needTxOut)
                        {
                            var tasks =
                                result[i].Transaction
                                     .Inputs
                                     .Select(txin => Task.Run(() =>
                                     {
                                         var parentTx = GetTransactions(false, new uint256[] { txin.PrevOut.Hash }).FirstOrDefault();
                                         if (parentTx == null)
                                         {
                                             IndexerTrace.MissingTransactionFromDatabase(txin.PrevOut.Hash);
                                             return null;
                                         }
                                         return parentTx.Transaction.Outputs[(int)txin.PrevOut.N];
                                     }))
                                     .ToArray();

                            Task.WaitAll(tasks);
                            if (tasks.All(t => t.Result != null))
                            {
                                var outputs = tasks.Select(t => t.Result).ToArray();
                                result[i].SpentCoins = outputs.Select((o, n) => new Spendable(result[i].Transaction.Inputs[n].PrevOut, o)).ToList();
                                entities[0].PreviousTxOuts.Clear();
                                entities[0].PreviousTxOuts.AddRange(outputs);
                                if (entities[0].IsLoaded)
                                {
                                    table.Execute(TableOperation.Merge(entities[0].CreateTableEntity()));
                                }
                            }
                        }

                        if (result[i].Transaction == null)
                            result[i] = null;
                    }
                });
            }
            catch (AggregateException ex)
            {
                throw ex.InnerException;
            }
            return result;
        }

        public ChainChangeEntry GetBestBlock()
        {
            var table = Configuration.GetChainTable();
            var query = new TableQuery<ChainChangeEntry.Entity>()
                        .Take(1);
            var entity = table.ExecuteQuery<ChainChangeEntry.Entity>(query).FirstOrDefault();
            if (entity == null)
                return null;
            return entity.ToObject();
        }

        public IEnumerable<ChainChangeEntry> GetChainChangesUntilFork(ChainedBlock currentTip, bool forkIncluded)
        {
            var table = Configuration.GetChainTable();
            var query = new TableQuery<ChainChangeEntry.Entity>();
            List<ChainChangeEntry> blocks = new List<ChainChangeEntry>();
            foreach (var block in table.ExecuteQuery(query).Select(e => e.ToObject()))
            {
                if (block.Height > currentTip.Height)
                    yield return block;
                else if (block.Height < currentTip.Height)
                {
                    currentTip = currentTip.FindAncestorOrSelf(block.Height);
                }

                if (block.Height == currentTip.Height)
                {
                    if (block.BlockId == currentTip.HashBlock)
                    {
                        if (forkIncluded)
                            yield return block;
                        break;
                    }
                    else
                    {
                        yield return block;
                        currentTip = currentTip.Previous;
                    }
                }
            }
        }
       

        public WalletBalanceChangeEntry[] GetWalletBalance(string walletId)
        {
            return new WalletBalanceChangeIndexer(Configuration).GetBalanceEntries(walletId, this, null);
        }


        public AddressBalanceChangeEntry[][] GetAllAddressBalances(BitcoinAddress[] addresses)
        {
            Helper.SetThrottling();
            AddressBalanceChangeEntry[][] result = new AddressBalanceChangeEntry[addresses.Length][];
            Parallel.For(0, addresses.Length,
            i =>
            {
                result[i] = GetAddressBalance(addresses[i]);
            });
            return result;
        }


        /// <summary>
        /// Fetch the spent txout of the balance entry
        /// </summary>
        /// <param name="entity">The entity to load</param>
        /// <returns>true if spent txout are loaded, false if one of the parent transaction is not yet indexed</returns>
        public bool LoadAddressBalanceChangeEntity(AddressBalanceChangeEntry.Entity entity)
        {
            return new AddressBalanceChangeIndexer(Configuration).LoadBalanceChangeEntity(entity, this, null);
        }

        /// <summary>
        /// Fetch the spent txout of the balance entry
        /// </summary>
        /// <param name="entity">The entity to load</param>
        /// <returns>true if spent txout are loaded, false if one of the parent transaction is not yet indexed</returns>

        public bool LoadWalletBalanceChangeEntity(WalletBalanceChangeEntry.Entity entity)
        {
            return new WalletBalanceChangeIndexer(Configuration).LoadBalanceChangeEntity(entity, this, null);
        }


        public AddressBalanceChangeEntry[] GetAddressBalance(BitcoinAddress address)
        {
            return GetAddressBalance(address.ID);
        }


        public AddressBalanceChangeEntry[] GetAddressBalance(TxDestination id)
        {
            return new AddressBalanceChangeIndexer(Configuration).GetBalanceEntries(Helper.EncodeId(id), this, null);
        }
        public AddressBalanceChangeEntry[] GetAddressBalance(KeyId keyId)
        {
            return GetAddressBalance((TxDestination)keyId);
        }
        public AddressBalanceChangeEntry[] GetAddressBalance(ScriptId scriptId)
        {
            return GetAddressBalance((ScriptId)scriptId);
        }
        public AddressBalanceChangeEntry[] GetAddressBalance(BitcoinScriptAddress scriptAddress)
        {
            return GetAddressBalance(scriptAddress.ID);
        }
        public AddressBalanceChangeEntry[] GetAddressBalance(PubKey pubKey)
        {
            return GetAddressBalance(pubKey.ID);
        }

        public void AddWalletRuleTypeConverter<T>() where T : WalletRule, new()
        {
            AddWalletRuleTypeConverter(new T().TypeName, () => new T());
        }

        public void AddWalletRuleTypeConverter(string typeName, Func<WalletRule> createEmptyRule)
        {
            _Rules.Add(typeName, createEmptyRule);
        }
        Dictionary<string, Func<WalletRule>> _Rules = new Dictionary<string, Func<WalletRule>>();
        public WalletRuleEntry[] GetWalletRules(string walletId)
        {
            var table = Configuration.GetWalletRulesTable();
            var searchedEntity = new WalletRuleEntry(walletId, null).CreateTableEntity();
            var query = new TableQuery()
                                    .Where(TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, searchedEntity.PartitionKey));
            return
                table.ExecuteQuery(query)
                 .Select(e => new WalletRuleEntry(e, this))
                 .ToArray();
        }


        public WalletRuleEntry[] GetAllWalletRules()
        {
            return
                Configuration.GetWalletRulesTable()
                .ExecuteQuery(new TableQuery())
                .Select(e => new WalletRuleEntry(e, this))
                .ToArray();
        }

        internal WalletRule DeserializeRule(string str)
        {
            JsonTextReader reader = new JsonTextReader(new StringReader(str));
            reader.Read();
            reader.Read();
            reader.Read();
            var type = (string)reader.Value;
            if (!_Rules.ContainsKey(type))
                throw new InvalidOperationException("Type " + type + " not registered with AzureIndexer.AddWalletRuleTypeConverter");
            var rule = _Rules[type]();
            reader.Read();
            rule.ReadJson(reader, true);
            return rule;
        }
    }
}
