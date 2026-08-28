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
/// (StartPage + "Actions") and routes the page load to the standard
/// <c>OnLoadAsync(id, parent)</c> hook; the object is scaffolded from the model via
/// <c>IManager</c> (the same idiom dialog POs use) rather than loaded from the database. Served
/// read-only under the type-level <c>Read/StartPage</c> right (see security.json). The requested
/// id is deliberately ignored: whatever the menu declared, the caller gets today's numbers.
/// </summary>
public partial class StartPageActions
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;

    public async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        var obj = manager.GetPersistentObject("StartPage");

        var peopleCount = await session.Query<Person>().CountAsync();
        var companyCount = await session.Query<Company>().CountAsync();
        var carCount = await session.Query<Car>().CountAsync();

        obj["Welcome"].Value =
            $"Welcome to the Spark demo!\n" +
            $"This page is composed server-side by StartPageActions.OnLoadAsync — " +
            $"there is no StartPage document (or even a StartPage class) anywhere. " +
            $"It was requested as '{id}', which the page is free to ignore.";
        obj["PeopleCount"].Value = peopleCount;
        obj["CompanyCount"].Value = companyCount;
        obj["CarCount"].Value = carCount;
        obj.Breadcrumb = $"Start — {peopleCount + companyCount + carCount} records";

        return obj;
    }

    /// <summary>
    /// A <b>composed query</b> (#327): one row per collection, with its live count. Nothing backs
    /// these rows — there is no StartPage document, no StartPage class, and no collection to read
    /// them from. The framework maps each returned object against the StartPage model's
    /// <c>ShowedOn.Query</c> attributes, which is where the grid's columns come from.
    /// <para>
    /// Because a row is computed rather than stored, <b>row-level security does not run over these
    /// rows and cannot</b>: there is no document to re-judge and no stored value to redact against.
    /// This method is therefore the only thing deciding what a caller sees — which is exactly what
    /// the startup diagnostic for this query says out loud. Here that is trivially safe (three
    /// counts, no per-caller data); over anything with owners, the filtering would have to be
    /// written right here.
    /// </para>
    /// </summary>
    public async Task<IEnumerable<CollectionRow>> GetCollections()
    {
        // Ids are required and must be unique — a row's id is how selection and custom actions
        // name it, and the framework refuses a null or repeated one rather than collapsing the grid.
        return
        [
            new CollectionRow("collections/people", "People", await session.Query<Person>().CountAsync()),
            new CollectionRow("collections/companies", "Companies", await session.Query<Company>().CountAsync()),
            new CollectionRow("collections/cars", "Cars", await session.Query<Car>().CountAsync()),
        ];
    }
}

/// <summary>
/// A computed row. An ordinary record: the mapper reads its properties by the names the model
/// declares (<c>Collection</c>, <c>Records</c>), and <c>Id</c> is the row identity.
/// </summary>
public sealed record CollectionRow(string Id, string Collection, int Records);
