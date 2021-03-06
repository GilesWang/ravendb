//-----------------------------------------------------------------------
// <copyright file="SuggestionQueryRunner.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.IO;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Microsoft.Isam.Esent.Interop;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Database.Actions;
using Raven.Database.Extensions;
using Raven.Json.Linq;
using SpellChecker.Net.Search.Spell;
using Task = System.Threading.Tasks.Task;

namespace Raven.Database.Queries
{
    public class SuggestionQueryRunner
    {
        private readonly DocumentDatabase database;

        public SuggestionQueryRunner(DocumentDatabase database)
        {
            this.database = database;
        }

        public SuggestionQueryResult ExecuteSuggestionQuery(string indexName, SuggestionQuery suggestionQuery)
        {
            if (suggestionQuery == null) throw new ArgumentNullException("suggestionQuery");
            if (string.IsNullOrWhiteSpace(suggestionQuery.Term)) throw new ArgumentNullException("suggestionQuery.Term");
            if (string.IsNullOrWhiteSpace(indexName)) throw new ArgumentNullException("indexName");
            if (string.IsNullOrWhiteSpace(suggestionQuery.Field)) throw new ArgumentNullException("suggestionQuery.Field");

            suggestionQuery.MaxSuggestions = Math.Min(suggestionQuery.MaxSuggestions, database.Configuration.MaxPageSize);

            if (suggestionQuery.MaxSuggestions <= 0) suggestionQuery.MaxSuggestions = SuggestionQuery.DefaultMaxSuggestions;
            if (suggestionQuery.Accuracy.HasValue && (suggestionQuery.Accuracy.Value <= 0f || suggestionQuery.Accuracy.Value > 1f))
                suggestionQuery.Accuracy = SuggestionQuery.DefaultAccuracy;

            if (suggestionQuery.Accuracy.HasValue == false)
                suggestionQuery.Accuracy = SuggestionQuery.DefaultAccuracy;
            if (suggestionQuery.Distance.HasValue == false)
                suggestionQuery.Distance = StringDistanceTypes.Default;

            var definition = database.IndexDefinitionStorage.GetIndexDefinition(indexName);
            var indexExtensionKey =
                MonoHttpUtility.UrlEncode(suggestionQuery.Field + "-" + suggestionQuery.Distance + "-" + suggestionQuery.Accuracy);
            var indexExtension = database.IndexStorage.GetIndexExtensionByPrefix(indexName, indexExtensionKey) as SuggestionQueryIndexExtension;

            IndexSearcher currentSearcher;
            using (database.IndexStorage.GetCurrentIndexSearcher(definition.IndexId, out currentSearcher))
            {
                if (currentSearcher == null)
                {
                    throw new InvalidOperationException("Could not find current searcher");
                }
                var indexReader = currentSearcher.IndexReader;

                if (indexExtension != null)
                    return indexExtension.Query(suggestionQuery, indexReader);
                var indexInstance = database.IndexStorage.GetIndexInstance(indexName);
                if(indexInstance == null)
                    throw new IndexDoesNotExistsException("Could not find index " + indexName);

                var suggestionQueryIndexExtension = new SuggestionQueryIndexExtension(
                    indexInstance,
                    database.WorkContext,
                    Path.Combine(database.Configuration.IndexStoragePath, "Raven-Suggestions", indexName, indexExtensionKey),
                    GetStringDistance((StringDistanceTypes) suggestionQuery.Distance),
                    indexReader.Directory() is RAMDirectory,
                    suggestionQuery.Field,
                    (float) suggestionQuery.Accuracy);

                database.IndexStorage.SetIndexExtension(indexName, indexExtensionKey, suggestionQueryIndexExtension);

                long _;
                var task = Task.Factory.StartNew(() => suggestionQueryIndexExtension.Init(indexReader));
                database.Tasks.AddTask(task, new TaskBasedOperationState(task), new TaskActions.PendingTaskDescription
                                                           {
                                                               Payload = indexName,
                                                               TaskType = TaskActions.PendingTaskType.SuggestionQuery,
                                                               StartTime = SystemTime.UtcNow
                                                           }, out _);

                // wait for a bit for the suggestions to complete, but not too much (avoid IIS resets)
                task.Wait(15000, database.WorkContext.CancellationToken);

                return suggestionQueryIndexExtension.Query(suggestionQuery, indexReader);
            }
        }

        [CLSCompliant(false)]
        public static StringDistance GetStringDistance(StringDistanceTypes distanceAlg)
        {
            switch (distanceAlg)
            {
                case StringDistanceTypes.NGram:
                    return new NGramDistance();
                case StringDistanceTypes.JaroWinkler:
                    return new JaroWinklerDistance();
                default:
                    return new LevenshteinDistance();
            }
        }
    }
}
