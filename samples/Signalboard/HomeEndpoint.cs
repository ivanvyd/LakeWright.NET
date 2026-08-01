namespace Signalboard;

/// <summary>
/// A landing page that tells you what to try, because a sample you cannot drive is a screenshot.
/// </summary>
public static class HomeEndpoint
{
    public static IEndpointRouteBuilder MapSignalboardHome(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/", () => Results.Text(Page, "text/plain"))
            .AllowAnonymous()
            .ExcludeFromDescription();

        return routes;
    }

    private static string Page => $$"""
        Signalboard — the Lakewright.NET sample

        Two organizations are seeded. Authentication is a header, so you can be anyone:

          Acme    {{DemoTenants.Acme.Value}}
                  {{DemoTenants.Alice}}  (Admin)
                  {{DemoTenants.Vera}}   (Viewer)

          Globex  {{DemoTenants.Globex.Value}}
                  {{DemoTenants.Bob}}    (Admin)

        Start an operation as Alice:

          curl -i -X POST http://localhost:8080/organizations/{{DemoTenants.Acme.Value}}/operations \
            -H "X-Demo-User: {{DemoTenants.Alice}}" -H "Content-Type: application/json" \
            -d '{"kind":"analysis"}'

        You get 202 and a Location header. Read it back as Alice, and it is there.

        Now the point of the whole project. Take that operation id and ask for it as Bob:

          curl -i http://localhost:8080/organizations/{{DemoTenants.Acme.Value}}/operations/<id> \
            -H "X-Demo-User: {{DemoTenants.Bob}}"

        404. Not 403 — a 403 would confirm the operation exists. Bob is not a member of Acme, so
        the tenant never resolves and the request stops before authorization is consulted.

        Try Vera, a Viewer at Acme, starting an operation:

          curl -i -X POST http://localhost:8080/organizations/{{DemoTenants.Acme.Value}}/operations \
            -H "X-Demo-User: {{DemoTenants.Vera}}" -H "Content-Type: application/json" \
            -d '{"kind":"analysis"}'

        403. She is a member, so the tenant resolves; she just lacks the role.

        Without a Databricks workspace configured the operation worker does not start, so operations
        stay Pending. That is the honest behaviour: nothing here fakes a run. Set
        Databricks:WorkspaceUrl, Databricks:WarehouseId, Databricks:Token and OperationWorker:JobId
        to see one reach Succeeded.

        OpenAPI: /openapi/v1.json
        """;
}
