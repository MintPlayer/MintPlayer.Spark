using MintPlayer.Spark.ReferenceData.Sources.UnitedStates;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataUnitedStatesExtensions
{
    /// <summary>
    /// The United States (PRD §4.4): sources <c>us-codes</c> (state and county codes), <c>us-county-changes</c> (county
    /// changes since 1970) and <c>us-addrfeat</c> (streets and non-authoritative ZIP Codes from the Census Bureau's
    /// address-range files), scheduled when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the U.S. mapper.
    /// </summary>
    public static ReferenceDataBuilder AddUnitedStates(this ReferenceDataBuilder builder)
        => builder
            .AddSource<UnitedStatesCodesSource>()
            .AddSource<UnitedStatesCountyChangesSource>()
            .AddSource<UnitedStatesAddressRangesSource>()
            .AddMapper<UnitedStatesMapper>();
}
