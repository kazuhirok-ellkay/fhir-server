﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hl7.FhirPath.Expressions;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Converters;
using Microsoft.Health.Fhir.Core.Features.Search.Parameters;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.ValueSets;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search
{
    public class SearchConverterForAllSearchTypes : IClassFixture<SearchParameterFixtureData>
    {
        private readonly SearchParameterFixtureData _fixture;
        private readonly ITestOutputHelper _outputHelper;

        public SearchConverterForAllSearchTypes(SearchParameterFixtureData fixture, ITestOutputHelper outputHelper)
        {
            _fixture = fixture;
            _outputHelper = outputHelper;
        }

        [Theory]
        [MemberData(nameof(GetAllSearchParameters))]
        public async Task CheckSearchParameter(
            string resourceType,
            IEnumerable<SearchParameterInfo> parameters)
        {
            foreach (var parameterInfo in parameters)
            {
                var fhirPath = parameterInfo.Expression;
                var parameterName = parameterInfo.Name;
                var searchParamType = parameterInfo.Type;

                _outputHelper.WriteLine("** Evaluating: " + fhirPath);

                var converters =
                    await GetConvertsForSearchParameters(resourceType, parameterInfo);

                Assert.True(
                    converters.Any(x => x.hasConverter),
                    $"{resourceType}.{parameterName} ({converters.First().result.FhirNodeType}=>{searchParamType}) was not able to be mapped.");

                string listedTypes = string.Join(",", converters.Select(x => x.result.FhirNodeType));
                _outputHelper.WriteLine($"Info: {parameterName} ({searchParamType}) found {listedTypes} types ({converters.Count}).");

                foreach (var result in converters.Where(x => x.hasConverter || !parameterInfo.IsPartiallySupported))
                {
                    var found = (await SearchParameterFixtureData.GetFhirNodeToSearchValueTypeConverterManagerAsync()).TryGetConverter(result.result.FhirNodeType, SearchIndexer.GetSearchValueTypeForSearchParamType(result.result.SearchParamType), out var converter);

                    var converterText = found ? converter.GetType().Name : "None";
                    string searchTermMapping = $"Search term '{parameterName}' ({result.result.SearchParamType}) mapped to '{result.result.FhirNodeType}', converter: {converterText}";
                    _outputHelper.WriteLine(searchTermMapping);

                    Assert.True(found, searchTermMapping);
                }
            }
        }

        [Fact]
        public async Task ListAllUnsupportedTypes()
        {
            var unsupported = new UnsupportedSearchParameters();

            SearchParameterDefinitionManager manager = await SearchParameterFixtureData.CreateSearchParameterDefinitionManagerAsync(ModelInfoProvider.Instance);

            var resourceAndSearchParameters = ModelInfoProvider.Instance
                .GetResourceTypeNames()
                .Select(resourceType => (resourceType, parameters: manager.GetSearchParameters(resourceType)));

            foreach (var searchParameterRow in resourceAndSearchParameters)
            {
                foreach (SearchParameterInfo parameterInfo in searchParameterRow.parameters)
                {
                    if (parameterInfo.Name != "_type")
                    {
                        var converters = await GetConvertsForSearchParameters(searchParameterRow.resourceType, parameterInfo);

                        if (converters.All(x => x.hasConverter == false))
                        {
                            unsupported.Unsupported.Add(parameterInfo.Url);
                        }
                        else if (converters.Any(x => x.hasConverter == false))
                        {
                            unsupported.PartialSupport.Add(parameterInfo.Url);
                        }
                    }
                }
            }

            // Print the current state to the console
            _outputHelper.WriteLine(JsonConvert.SerializeObject(unsupported, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented,
            }));

            // Check this against the list already in the system:
            var systemUnsupported = new UnsupportedSearchParameters();
            foreach (var searchParameter in resourceAndSearchParameters.SelectMany(x => x.parameters))
            {
                if (!searchParameter.IsSupported)
                {
                    systemUnsupported.Unsupported.Add(searchParameter.Url);
                }
                else if (searchParameter.IsPartiallySupported)
                {
                    systemUnsupported.PartialSupport.Add(searchParameter.Url);
                }
            }

            // Expect that the static file "unsupported-search-parameters.json" equals the generated list
            Assert.Equal(systemUnsupported.Unsupported, unsupported.Unsupported);
            Assert.Equal(systemUnsupported.PartialSupport, unsupported.PartialSupport);
        }

        private async Task<IReadOnlyCollection<(SearchParameterTypeResult result, bool hasConverter, IFhirNodeToSearchValueTypeConverter converter)>> GetConvertsForSearchParameters(
            string resourceType,
            SearchParameterInfo parameterInfo)
        {
            var parsed = SearchParameterFixtureData.Compiler.Parse(parameterInfo.Expression);

            SearchParameterDefinitionManager searchParameterDefinitionManager = await _fixture.GetSearchDefinitionManagerAsync();

            (SearchParamType Type, Expression, Uri DefinitionUrl)[] componentExpressions = parameterInfo.Component
                .Select(x => (searchParameterDefinitionManager.UrlLookup[x.DefinitionUrl].Type,
                    SearchParameterFixtureData.Compiler.Parse(x.Expression),
                    x.DefinitionUrl))
                .ToArray();

            SearchParameterTypeResult[] results = SearchParameterToTypeResolver.Resolve(
                resourceType,
                (parameterInfo.Type, parsed, parameterInfo.Url),
                componentExpressions).ToArray();

            var fhirNodeToSearchValueTypeConverterManager = await SearchParameterFixtureData.GetFhirNodeToSearchValueTypeConverterManagerAsync();

            var converters = results
                .Select(result => (
                    result,
                    hasConverter: fhirNodeToSearchValueTypeConverterManager.TryGetConverter(
                        result.FhirNodeType,
                        SearchIndexer.GetSearchValueTypeForSearchParamType(result.SearchParamType),
                        out IFhirNodeToSearchValueTypeConverter converter),
                    converter))
                .ToArray();

            return converters;
        }

        public static IEnumerable<object[]> GetAllSearchParameters()
        {
            Task<SearchParameterDefinitionManager> searchParameterDefinitionManagerTask = SearchParameterFixtureData.CreateSearchParameterDefinitionManagerAsync(ModelInfoProvider.Instance);

            // XUnit does not currently support async signatures for MemberDataAttributes. Until it does we need to block on
            // this task, which could cause a deadlock, but we know that the task should have completed synchronously,
            // so there should not be a problem.

            Assert.True(searchParameterDefinitionManagerTask.IsCompleted);
            var manager = searchParameterDefinitionManagerTask.GetAwaiter().GetResult(); // see assertion above

            var values = ModelInfoProvider.Instance
                .GetResourceTypeNames()
                .Select(resourceType => (resourceType, parameters: manager.GetSearchParameters(resourceType)));

            foreach ((string resourceType, IEnumerable<SearchParameterInfo> parameters) row in values)
            {
                yield return new object[] { row.resourceType, row.parameters.Where(x => x.Name != "_type" && x.IsSupported) };
            }
        }
    }
}
