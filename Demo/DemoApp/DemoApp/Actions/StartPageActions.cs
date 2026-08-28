using DemoApp.Library.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace DemoApp.Actions;

/// <summary>
/// The composed landing page (#324): the "Start" program unit points at the StartPage type —
/// which exists ONLY as App_Data/Model/StartPage.json, no CLR entity, no documents — and this
/// class builds the page: greeting plus live collection counts. The framework finds it by name
/// (StartPage + "Actions") and, since the type is virtual, routes the page load to the PO-shaped
/// <c>OnLoadAsync(PersistentObject)</c> below. Served read-only under the type-level
/// <c>Read/StartPage</c> right (see security.json). The object arrives scaffolded with
/// <c>obj.Id</c> = the requested id, which this page deliberately ignores: whatever the menu
/// declared, the caller gets today's numbers.
/// </summary>
public partial class StartPageActions
{
    [Inject] private readonly IAsyncDocumentSession session;

    public async Task OnLoadAsync(PersistentObject obj)
    {
        var peopleCount = await session.Query<Person>().CountAsync();
        var companyCount = await session.Query<Company>().CountAsync();
        var carCount = await session.Query<Car>().CountAsync();

        obj["Welcome"].Value =
            $"Welcome to the Spark demo!\n" +
            $"This page is composed server-side by StartPageActions.OnLoadAsync — " +
            $"there is no StartPage document (or even a StartPage class) anywhere. " +
            $"It was requested as '{obj.Id}', which the page is free to ignore.";
        obj["PeopleCount"].Value = peopleCount;
        obj["CompanyCount"].Value = companyCount;
        obj["CarCount"].Value = carCount;
        obj.Breadcrumb = $"Start — {peopleCount + companyCount + carCount} records";
    }
}
