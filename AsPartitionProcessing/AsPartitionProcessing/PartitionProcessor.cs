﻿using System;
using System.Collections.Generic;
using Microsoft.AnalysisServices.Tabular;


//-----------
//ASSUMPTIONS
//Rolling window. Removes oldest partition on increment
//Depends on date keys in source to be integers formatted as yyyymmdd
//Source queries take the form "SELECT * FROM <SourceTable> WHERE FLOOR(<SourceColumn> / 100) = <yyyymm>" (monthly)
//Template partition exists with same name as table
//Non-template partitions have name of the format yyyy (yearly), yyyymm (monthly), yyyymmdd (daily)
//-----------


namespace AsPartitionProcessing
{
    /// <summary>
    /// Delegate to allow client to pass in a custom logging method
    /// </summary>
    /// <param name="message">The message to be logged</param>
    /// <param name="modelConfiguration">Configuration info for the model</param>
    public delegate void LogMessageDelegate(string message, ModelConfiguration modelConfiguration);

    /// <summary>
    /// Processor of partitions in AS tabular models
    /// </summary>
    public static class PartitionProcessor
    {
        private static ModelConfiguration _modelConfiguration;
        private static LogMessageDelegate _messageLogger;

        /// <summary>
        /// Partitions tables in a tabular model based on configuration
        /// </summary>
        /// <param name="modelConfiguration">Configuration info for the model</param>
        /// <param name="messageLogger">Pointer to logging method</param>
        public static void PerformProcessing(ModelConfiguration modelConfiguration, LogMessageDelegate messageLogger)
        {
            _modelConfiguration = modelConfiguration;
            _messageLogger = messageLogger;

            Server server = new Server();
            try
            {
                Database database;
                Connect(server, out database);

                Console.ForegroundColor = ConsoleColor.White;
                LogMessage($"Start: {DateTime.Now.ToString("hh:mm:ss tt")}", false);
                LogMessage($"Server: {_modelConfiguration.AnalysisServicesServer}", false);
                LogMessage($"Database: {_modelConfiguration.AnalysisServicesDatabase}", false);
                Console.ForegroundColor = ConsoleColor.Yellow;

                foreach (TableConfiguration tableConfiguration in _modelConfiguration.TableConfigurations)
                {
                    Table table = database.Model.Tables.Find(tableConfiguration.AnalysisServicesTable);
                    if (table == null)
                    {
                        throw new Microsoft.AnalysisServices.ConnectionException($"Could not connect to table {tableConfiguration.AnalysisServicesTable}.");
                    }

                    if (tableConfiguration.PartitioningConfigurations.Count == 0)
                    {
                        //Non-partitioned table. Process at table level.
                        LogMessage("", false);
                        LogMessage($"Non-partitioned processing for table {tableConfiguration.AnalysisServicesTable}", false);
                        LogMessage(new String('-', tableConfiguration.AnalysisServicesTable.Length + 37), false);

                        if (_modelConfiguration.IncrementalOnline)
                        {
                            LogMessage($"Process table {tableConfiguration.AnalysisServicesTable} /Full", true);
                            table.RequestRefresh(RefreshType.Full);
                        }
                        else
                        {
                            LogMessage($"Process table {tableConfiguration.AnalysisServicesTable} /DataOnly", true);
                            table.RequestRefresh(RefreshType.DataOnly);
                        }
                    }
                    else
                    {
                        //Non-partitioned table. Process based on partitioning configuration(s).
                        Partition templatePartition = table.Partitions.Find(tableConfiguration.AnalysisServicesTable);
                        if (templatePartition == null)
                        {
                            throw new Microsoft.AnalysisServices.ConnectionException($"Table {tableConfiguration.AnalysisServicesTable} does not contain a partition with the same name to act as the template partition.");
                        }

                        foreach (PartitioningConfiguration partitioningConfiguration in tableConfiguration.PartitioningConfigurations)
                        {
                            LogMessage("", false);
                            LogMessage($"Rolling-window partitioning for table {tableConfiguration.AnalysisServicesTable}", false);
                            LogMessage(new String('-', tableConfiguration.AnalysisServicesTable.Length + 38), false);

                            //Figure out what processing needs to be done
                            List<string> partitionKeysCurrent = GetPartitionKeysTable(table, partitioningConfiguration.Granularity);
                            List<string> partitionKeysNew = GetPartitionKeys(false, partitioningConfiguration, partitioningConfiguration.Granularity);
                            List<string> partitionKeysForProcessing = GetPartitionKeys(true, partitioningConfiguration, partitioningConfiguration.Granularity);
                            DisplayPartitionRange(partitionKeysCurrent, true, partitioningConfiguration.Granularity);
                            DisplayPartitionRange(partitionKeysNew, false, partitioningConfiguration.Granularity);
                            LogMessage("", false);
                            LogMessage("=>Actions & progress:", false);

                            //Check for old partitions that need to be removed
                            foreach (string partitionKey in partitionKeysCurrent)
                            {
                                if (Convert.ToInt32(partitionKey) < Convert.ToInt32(partitionKeysNew[0]))
                                {
                                    LogMessage($"Remove old partition       {DateFormatPartitionKey(partitionKey, partitioningConfiguration.Granularity)}", true);
                                    table.Partitions.Remove(partitionKey);
                                }
                            }

                            //Process partitions
                            string selectQueryTemplate;
                            switch (partitioningConfiguration.Granularity)
                            {
                                case Granularity.Daily:
                                    selectQueryTemplate = "SELECT * FROM {0} WHERE {1} = {2} ORDER BY {1}";
                                    break;
                                case Granularity.Monthly:
                                    selectQueryTemplate = "SELECT * FROM {0} WHERE FLOOR({1} / 100) = {2} ORDER BY {1}";
                                    break;
                                default: //Granularity.Yearly:
                                    selectQueryTemplate = "SELECT * FROM {0} WHERE FLOOR({1} / 10000) = {2} ORDER BY {1}";
                                    break;
                            }

                            foreach (string partitionKey in partitionKeysForProcessing)
                            {
                                Partition partitionToProcess = table.Partitions.Find(partitionKey);

                                if (partitionToProcess == null)
                                {
                                    //Doesn't exist so create it
                                    partitionToProcess = new Partition();
                                    templatePartition.CopyTo(partitionToProcess);
                                    partitionToProcess.Name = partitionKey;
                                    ((QueryPartitionSource)partitionToProcess.Source).Query = String.Format(selectQueryTemplate, partitioningConfiguration.SourceTableName, partitioningConfiguration.SourcePartitionColumn, partitionKey);
                                    table.Partitions.Add(partitionToProcess);
                                    LogMessage($"Create new partition       {DateFormatPartitionKey(partitionKey, partitioningConfiguration.Granularity)}", true);

                                    if (!_modelConfiguration.InitialSetUp)
                                    {
                                        IncrementalProcessPartition(partitionKey, partitionToProcess, partitioningConfiguration.Granularity);
                                    }
                                }
                                else if (!_modelConfiguration.InitialSetUp)
                                {
                                    //Existing partition for processing
                                    IncrementalProcessPartition(partitionKey, partitionToProcess, partitioningConfiguration.Granularity);
                                }

                                if (_modelConfiguration.InitialSetUp)
                                {
                                    if (partitionToProcess.State != ObjectState.Ready)
                                    {
                                        //Process new partitions sequentially during initial setup so don't run out of memory
                                        LogMessage($"Sequentially process       {DateFormatPartitionKey(partitionKey, partitioningConfiguration.Granularity)} /DataOnly", true);
                                        partitionToProcess.RequestRefresh(RefreshType.DataOnly);
                                        database.Model.SaveChanges();
                                    }
                                    else
                                    {
                                        //Partition already exists during initial setup (and is fully processed), so ignore it
                                        LogMessage($"Partition {DateFormatPartitionKey(partitionKey, partitioningConfiguration.Granularity)} already exists and is processed", true);
                                    }
                                }
                            }
                        }

                        //Ensure template partition doesn't contain any data
                        if (_modelConfiguration.InitialSetUp)
                        {
                            ((QueryPartitionSource)templatePartition.Source).Query = String.Format("SELECT * FROM {0} WHERE 0=1", tableConfiguration.PartitioningConfigurations[0].SourceTableName); //assuming the same for all partitioning configurations
                            templatePartition.RequestRefresh(RefreshType.DataOnly);
                        }
                    }

                    //If processing tables sequentially (but all partitions being done in parallel), then save changes now
                    if (!_modelConfiguration.IncrementalParallelTables)
                    {
                        LogMessage($"Save changes for table {tableConfiguration.AnalysisServicesTable} ...", true);
                        database.Model.SaveChanges();
                    }
                }

                //Commit the data changes, and bring model back online if necessary

                LogMessage("", false);
                LogMessage("Final operations", false);
                LogMessage(new String('-', 16), false);

                if (_modelConfiguration.IncrementalParallelTables)
                {
                    LogMessage("Save changes ...", true);
                    database.Model.SaveChanges();
                }

                if (_modelConfiguration.InitialSetUp || (!_modelConfiguration.InitialSetUp && !_modelConfiguration.IncrementalOnline))
                {
                    LogMessage("Recalc model to bring back online ...", true);

                    database.Model.RequestRefresh(RefreshType.Calculate);
                    database.Model.SaveChanges();
                }

                Console.ForegroundColor = ConsoleColor.White;
                LogMessage("", false);
                LogMessage("Finish: " + DateTime.Now.ToString("hh:mm:ss tt"), false);
            }
            catch (Exception exc)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                LogMessage("", false);
                LogMessage($"Exception occurred: {DateTime.Now.ToString("hh:mm:ss tt")}", false);
                LogMessage($"Exception message: {exc.Message}", false);
                LogMessage($"Inner exception message: {exc.InnerException.Message}", false);
            }
            finally
            {
                try
                {
                    _modelConfiguration = null;
                    _messageLogger = null;
                    if (server != null) server.Disconnect();
                }
                catch { }
            }
        }

        private static void IncrementalProcessPartition(string partitionKey, Partition partitionToProcess, Granularity granularity)
        {
            if (_modelConfiguration.IncrementalOnline)
            {
                LogMessage($"Parallel process partition {DateFormatPartitionKey(partitionKey, granularity)} /Full", true);
                partitionToProcess.RequestRefresh(RefreshType.Full);
            }
            else
            {
                LogMessage($"Parallel process partition {DateFormatPartitionKey(partitionKey, granularity)} /DataOnly", true);
                partitionToProcess.RequestRefresh(RefreshType.DataOnly);
            }
        }

        private static void LogMessage(string message, bool indented)
        {
            _messageLogger($"{(indented ? new String(' ', 3) : "")}{message}", _modelConfiguration);
        }

        private static string DateFormatPartitionKey(string partitionKey, Granularity granularity)
        {
            string returnVal = partitionKey.Substring(0, 4);

            try
            {
                if (granularity == Granularity.Monthly || granularity == Granularity.Daily)
                {
                    returnVal += "-" + partitionKey.Substring(4, 2);
                }

                if (granularity == Granularity.Daily)
                {
                    returnVal += "-" + partitionKey.Substring(6, 2);
                }
            }
            catch (ArgumentOutOfRangeException exc)
            {
                throw new InvalidOperationException($"Failed to derive date from partition key. Check the key {partitionKey} matches {Convert.ToString(granularity)} granularity.");
            }

            return returnVal;
        }

        private static void DisplayPartitionRange(List<string> partitionKeys, bool current, Granularity granularity)
        {
            LogMessage("", false);

            if (partitionKeys.Count > 0)
            {
                LogMessage($"=>{(current ? "Current" : "New")} partition range ({Convert.ToString(granularity)}):", false);
                LogMessage($"MIN partition:   {DateFormatPartitionKey(partitionKeys[0], granularity)}", true);
                LogMessage($"MAX partition:   {DateFormatPartitionKey(partitionKeys[partitionKeys.Count - 1], granularity)}", true);
                LogMessage($"Partition count: {partitionKeys.Count}", true);
            }
            else
            {
                LogMessage("=>Table not yet partitioned", false);
            }
        }

        private static void Connect(Server server, out Database database)
        {
            //Connect and get main objects
            string serverConnectionString;
            if (_modelConfiguration.IntegratedAuth)
                serverConnectionString = $"Provider=MSOLAP;Data Source={_modelConfiguration.AnalysisServicesServer};";
            else
            {
                serverConnectionString = $"Provider=MSOLAP;Data Source={_modelConfiguration.AnalysisServicesServer};User ID={_modelConfiguration.UserName};Password={_modelConfiguration.Password};Persist Security Info=True;Impersonation Level=Impersonate;";
            }
            server.Connect(serverConnectionString);

            database = server.Databases.FindByName(_modelConfiguration.AnalysisServicesDatabase);
            if (database == null)
            {
                throw new Microsoft.AnalysisServices.ConnectionException($"Could not connect to database {_modelConfiguration.AnalysisServicesDatabase}.");
            }
        }

        private static List<string> GetPartitionKeys(bool forProcessing, PartitioningConfiguration partitioningConfiguration, Granularity granularity) 
        {
            //forProcessing: false to return complete target set of partitions, true to return partitons to be processed (may be less if incremental mode).

            List<string> partitionKeys = new List<string>();
            int numberOfPartitions = (forProcessing && !_modelConfiguration.InitialSetUp ? partitioningConfiguration.NumberOfPartitionsForIncrementalProcess : partitioningConfiguration.NumberOfPartitionsFull);

            for (int i = numberOfPartitions - 1; i >= 0; i--)
            {
                DateTime periodToAdd;
                switch (granularity)
                {
                    case Granularity.Daily:
                        periodToAdd = partitioningConfiguration.MaxDate.AddDays(-i);
                        partitionKeys.Add(Convert.ToString((periodToAdd.Year * 100 + periodToAdd.Month) * 100 + periodToAdd.Day));
                        break;
                    case Granularity.Monthly:
                        periodToAdd = partitioningConfiguration.MaxDate.AddMonths(-i);
                        partitionKeys.Add(Convert.ToString(periodToAdd.Year * 100 + periodToAdd.Month));
                        break;
                    default: //Granularity.Yearly:
                        periodToAdd = partitioningConfiguration.MaxDate.AddYears(-i);
                        partitionKeys.Add(Convert.ToString(periodToAdd.Year));
                        break;
                }
            }
            partitionKeys.Sort();

            return partitionKeys;
        }

        private static List<string> GetPartitionKeysTable(Table table, Granularity granularity)
        {
            List<string> partitionKeysExisting = new List<string>();

            foreach (Partition partition in table.Partitions)
            {
                //Ignore partitions that don't follow the convention yyyy, yyyymm or yyyymmdd
                int partitionKey;
                if (
                      ( (partition.Name.Length == 4 && granularity == Granularity.Yearly) ||
                        (partition.Name.Length == 6 && granularity == Granularity.Monthly) ||
                        (partition.Name.Length == 8 && granularity == Granularity.Daily)
                      ) && int.TryParse(partition.Name, out partitionKey)
                   )
                {
                    partitionKeysExisting.Add(Convert.ToString(partitionKey));
                }
            }
            partitionKeysExisting.Sort();

            return partitionKeysExisting;
        }
    }
}

