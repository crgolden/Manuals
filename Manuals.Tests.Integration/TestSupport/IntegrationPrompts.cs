namespace Manuals.Tests.Integration.TestSupport;

using System.Globalization;
using Microsoft.Extensions.Configuration;

internal sealed class IntegrationPrompts
{
    private IntegrationPrompts(string recallProduct, string manualRequestFormat)
    {
        RecallProduct = recallProduct;
        ManualRequestFormat = manualRequestFormat;
    }

    internal string RecallProduct { get; }

    internal string ManualRequestFormat { get; }

    internal static IntegrationPrompts From(IConfiguration configuration)
    {
        var section = configuration.GetRequiredSection(nameof(IntegrationPrompts));
        var recallProduct = section[nameof(RecallProduct)];
        var manualRequestFormat = section[nameof(ManualRequestFormat)];
        ArgumentException.ThrowIfNullOrWhiteSpace(recallProduct);
        ArgumentException.ThrowIfNullOrWhiteSpace(manualRequestFormat);
        return new IntegrationPrompts(recallProduct, manualRequestFormat);
    }

    internal string ManualRequest(string productModel) =>
        string.Format(CultureInfo.InvariantCulture, ManualRequestFormat, productModel);
}
