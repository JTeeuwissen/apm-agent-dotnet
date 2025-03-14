// Licensed to Elasticsearch B.V under
// one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using Xunit;

namespace Elastic.Apm.Tests.Utilities.Azure
{
	/// <summary>
	/// Attribute applied to a test that should be run by the test runner if Azure credentials are available
	/// </summary>
	public class AzureCredentialsFactAttribute : FactAttribute
	{
		public AzureCredentialsFactAttribute()
		{
			if (AzureCredentials.Instance is Unauthenticated)
				Skip = "Azure credentials not available. If running locally, run `az login` to login";
		}
	}

	public class CosmosDbFactAttribute : FactAttribute
	{
		public CosmosDbFactAttribute()
		{
			if (AzureCredentials.Instance is Unauthenticated)
				Skip = "Azure credentials not available. If running locally, run `az login` to login";
			if (TestEnvironment.IsCi)
				Skip = "CosmosDb tests are temporarily disabled on CI environments";
		}
	}
}
