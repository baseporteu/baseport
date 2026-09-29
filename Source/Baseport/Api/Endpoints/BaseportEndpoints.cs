namespace Baseport;

public static class BaseportEndpoints
{
    public static void MapBaseportEndpoints(this WebApplication app)
    {
        app.MapAuthEndpoints();
        app.MapUserAuthEndpoints();
        app.MapOidcEndpoints();
        app.MapClientErrorEndpoints();
        app.MapStorageEndpoints();
        app.MapTableEndpoints();
        app.MapFormEndpoints();
        app.MapActionEndpoints();
        app.MapAdminEndpoints();
        app.MapPublicApiEndpoints();
        app.MapTransactionEndpoints();
        app.MapFragmentEndpoints();
        app.MapConsoleEndpoints();
    }
}
