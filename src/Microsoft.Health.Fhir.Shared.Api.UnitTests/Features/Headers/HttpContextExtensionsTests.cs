﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Fhir.Api.Features.Headers;
using Microsoft.Health.Fhir.Api.Features.Resources;
using Microsoft.Health.Fhir.Core.Features;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence.Orchestration;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Features.Headers
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public sealed class HttpContextExtensionsTests
    {
        [Trait(Traits.Category, Categories.Bundle)]
        [Theory]
        [InlineData(BundleProcessingLogic.Sequential)]
        [InlineData(BundleProcessingLogic.Parallel)]
        public void WhenHttpContextDoesNotHaveCustomHeaders_ReturnDefaultValues(BundleProcessingLogic defaultAndExpectBundleProcessingLogic)
        {
            // Empty HttpContext simulates the case where no custom headers are set.
            HttpContext httpContext = GetFakeHttpContext();

            bool isLatencyOverEfficiencyEnabled = httpContext.IsLatencyOverEfficiencyEnabled();
            Assert.False(isLatencyOverEfficiencyEnabled);

            // Given different default values for the bundle processing logic, we expect the same value to be returned.
            BundleProcessingLogic bundleProcessingLogic = httpContext.GetBundleProcessingLogic(
                defaultBundleProcessingLogic: defaultAndExpectBundleProcessingLogic);
            Assert.Equal(defaultAndExpectBundleProcessingLogic, bundleProcessingLogic);

            // Empty header is expected to be assumed as valid.
            bool isBundleProcessingLogicValid = httpContext.IsBundleProcessingLogicValid();
            Assert.True(isBundleProcessingLogicValid);

            // #conditionalQueryParallelism
            ConditionalQueryProcessingLogic conditionalQueryProcessingLogic = httpContext.GetConditionalQueryProcessingLogic();
            Assert.Equal(ConditionalQueryProcessingLogic.Sequential, conditionalQueryProcessingLogic);
        }

        [Theory]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("false", false)]
        [InlineData("falsE", false)]
        [InlineData("FALSE", false)]
        [InlineData("2112", false)]
        [InlineData("true", true)]
        [InlineData("true ", true)]
        [InlineData("TRUE", true)]
        [InlineData(" TRUE ", true)]
        [InlineData("   tRuE   ", true)]
        public void WhenHttpContextHasCustomHeaders_ReturnIfLatencyOverEfficiencyIsEnabled(string value, bool isEnabled)
        {
            var httpHeaders = new Dictionary<string, string>() { { KnownHeaders.QueryLatencyOverEfficiency, value } };
            HttpContext httpContext = GetFakeHttpContext(httpHeaders);

            bool isLatencyOverEfficiencyEnabled = httpContext.IsLatencyOverEfficiencyEnabled();

            Assert.Equal(isEnabled, isLatencyOverEfficiencyEnabled);
        }

        [Theory]
        [InlineData("", ConditionalQueryProcessingLogic.Sequential)]
        [InlineData(null, ConditionalQueryProcessingLogic.Sequential)]
        [InlineData("sequential", ConditionalQueryProcessingLogic.Sequential)]
        [InlineData("sequential ", ConditionalQueryProcessingLogic.Sequential)]
        [InlineData("Sequential", ConditionalQueryProcessingLogic.Sequential)]
        [InlineData("2112", ConditionalQueryProcessingLogic.Sequential)]
        [InlineData("red barchetta", ConditionalQueryProcessingLogic.Sequential)]
        [InlineData("parallel", ConditionalQueryProcessingLogic.Parallel)]
        [InlineData("parallel  ", ConditionalQueryProcessingLogic.Parallel)]
        [InlineData("Parallel", ConditionalQueryProcessingLogic.Parallel)]
        [InlineData(" pArAllEl  ", ConditionalQueryProcessingLogic.Parallel)]
        [InlineData("PARALLEL", ConditionalQueryProcessingLogic.Parallel)]
        public void WhenHttpContextHasCustomHeaders_ReturnIfConditionalQueryProcessingLogicIsSet(string value, ConditionalQueryProcessingLogic processingLogic)
        {
            // #conditionalQueryParallelism

            var httpHeaders = new Dictionary<string, string>() { { KnownHeaders.ConditionalQueryProcessingLogic, value } };
            HttpContext httpContext = GetFakeHttpContext(httpHeaders);

            ConditionalQueryProcessingLogic conditionalQueryProcessingLogic = httpContext.GetConditionalQueryProcessingLogic();

            Assert.Equal(processingLogic, conditionalQueryProcessingLogic);
        }

        [Trait(Traits.Category, Categories.Bundle)]
        [Theory]
        [InlineData("", BundleProcessingLogic.Sequential)]
        [InlineData(null, BundleProcessingLogic.Sequential)]
        [InlineData("sequential", BundleProcessingLogic.Sequential)]
        [InlineData("sequential ", BundleProcessingLogic.Sequential)]
        [InlineData("Sequential", BundleProcessingLogic.Sequential)]
        [InlineData("2112", BundleProcessingLogic.Sequential)]
        [InlineData("red barchetta", BundleProcessingLogic.Sequential)]
        [InlineData("parallel", BundleProcessingLogic.Parallel)]
        [InlineData("parallel  ", BundleProcessingLogic.Parallel)]
        [InlineData("Parallel", BundleProcessingLogic.Parallel)]
        [InlineData(" pArAllEl  ", BundleProcessingLogic.Parallel)]
        [InlineData("PARALLEL", BundleProcessingLogic.Parallel)]
        public void WhenHttpContextHasCustomHeaders_ReturnIfBundleProcessingLogicIsSet(string value, BundleProcessingLogic processingLogic)
        {
            var httpHeaders = new Dictionary<string, string>() { { BundleOrchestratorNamingConventions.HttpHeaderBundleProcessingLogic, value } };
            HttpContext httpContext = GetFakeHttpContext(httpHeaders);

            BundleProcessingLogic bundleProcessingLogic = httpContext.GetBundleProcessingLogic(
                defaultBundleProcessingLogic: BundleProcessingLogic.Sequential);

            Assert.Equal(processingLogic, bundleProcessingLogic);
        }

        [Trait(Traits.Category, Categories.Bundle)]
        [Theory]
        [InlineData("2112")]
        [InlineData("red barchetta")]
        public void WhenHttpContextHasCustomHeaders_WithInvalidValues_ThatShouldBeIdentified(string value)
        {
            var httpHeaders = new Dictionary<string, string>() { { BundleOrchestratorNamingConventions.HttpHeaderBundleProcessingLogic, value } };
            HttpContext httpContext = GetFakeHttpContext(httpHeaders);

            bool isBundleProcessingLogicValid = httpContext.IsBundleProcessingLogicValid();
            Assert.False(isBundleProcessingLogicValid);
        }

        [Fact]
        public void WhenProvidedAFhirRequestContext_ThenDecorateItWithOptimizeConcurrency()
        {
            // #conditionalQueryParallelism

            IFhirRequestContext fhirRequestContext = new Core.UnitTests.Features.Context.DefaultFhirRequestContext()
            {
                BaseUri = new Uri("https://localhost/"),
                CorrelationId = Guid.NewGuid().ToString(),
                ResponseHeaders = new HeaderDictionary(),
                RequestHeaders = new HeaderDictionary(),
            };

            fhirRequestContext.DecorateRequestContextWithOptimizedConcurrency();

            Assert.True(fhirRequestContext.Properties.ContainsKey(KnownQueryParameterNames.OptimizeConcurrency));
            Assert.Equal(true, fhirRequestContext.Properties[KnownQueryParameterNames.OptimizeConcurrency]);
        }

        private static HttpContext GetFakeHttpContext(IReadOnlyDictionary<string, string> optionalHttpHeaders = default)
        {
            var httpContext = new DefaultHttpContext()
            {
                Request =
                {
                    Scheme = "https",
                    Host = new HostString("localhost"),
                    PathBase = new PathString("/"),
                },
            };

            if (optionalHttpHeaders != null)
            {
                foreach (var header in optionalHttpHeaders)
                {
                    httpContext.Request.Headers.Append(header.Key, new StringValues(header.Value));
                }
            }

            return httpContext;
        }
    }
}
