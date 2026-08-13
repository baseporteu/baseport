using System.Net;
using System.Text;
using Baseport.Client;
using Xunit;

namespace Baseport.Tests;

public class BaseportClientTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public Recorder Reply(HttpStatusCode status, string body)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private static string Token(string sub, long expiresAt)
    {
        static string Segment(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Segment("""{"alg":"ES256"}""")}." +
               $"{Segment($$"""{"sub":"{{sub}}","email":"jane@example.com","username":"jane","role":"user","exp":{{expiresAt}}}""")}.signature";
    }

    private static (BaseportClient Client, Recorder Handler) Build()
    {
        var handler = new Recorder();
        return (new BaseportClient("http://localhost:5299", new HttpClient(handler)), handler);
    }

    private static string TokenResponse(string authToken, string refreshToken, long expiresAt) =>
        $$"""{"auth_token":"{{authToken}}","refresh_token":"{{refreshToken}}","expires_at":{{expiresAt}}}""";

    [Fact]
    public async Task Signing_in_adopts_the_tokens_and_reads_the_user_out_of_the_claims()
    {
        var (client, handler) = Build();
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        handler.Reply(HttpStatusCode.OK, TokenResponse(Token("user1", expires), "refresh-1", expires));

        await client.LoginAsync("jane@example.com", "supersecret1", TestContext.Current.CancellationToken);

        Assert.Equal("http://localhost:5299/api/auth/v1/login", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("email_or_username", handler.Bodies[0]);
        Assert.Equal("user1", client.User!.Sub);
        Assert.Equal("jane", client.User.Username);
        Assert.Equal("refresh-1", client.Tokens!.RefreshToken);
    }

    [Fact]
    public async Task A_failed_call_carries_the_status_and_the_server_message()
    {
        var (client, handler) = Build();
        handler.Reply(HttpStatusCode.Unauthorized, """{"errors":["Incorrect credentials."]}""");

        var error = await Assert.ThrowsAsync<BaseportException>(() =>
            client.LoginAsync("jane", "wrong", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("Incorrect credentials.", error.Message);
    }

    [Fact]
    public async Task An_expired_token_is_refreshed_before_the_call_that_needed_it()
    {
        var (client, handler) = Build();
        var expired = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds();
        var fresh = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        handler.Reply(HttpStatusCode.OK, TokenResponse(Token("user1", expired), "refresh-1", expired));
        await client.LoginAsync("jane", "supersecret1", TestContext.Current.CancellationToken);

        handler.Reply(HttpStatusCode.OK, TokenResponse(Token("user1", fresh), "refresh-2", fresh));
        handler.Reply(HttpStatusCode.OK, """{"id":"rec1","createdAt":"2026-08-13T10:00:00Z","data":{"title":"x"}}""");

        var record = await client.Records("notes").ReadAsync("rec1", TestContext.Current.CancellationToken);

        Assert.Equal("http://localhost:5299/api/auth/v1/refresh", handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("refresh-2", client.Tokens!.RefreshToken);
        Assert.Equal($"Bearer {Token("user1", fresh)}", handler.Requests[2].Headers.Authorization!.ToString());
        Assert.Equal("rec1", record.Id);
    }

    [Fact]
    public async Task Listing_pages_through_the_published_endpoint_name()
    {
        var (client, handler) = Build();
        client.UseApiToken("static-token");
        handler.Reply(HttpStatusCode.OK,
            """{"rows":[{"id":"rec1","createdAt":"2026-08-13T10:00:00Z","data":{"title":"x"}}],"page":2,"pageSize":5,"total":6,"totalPages":2,"hasMore":false}""");

        var page = await client.Records("notes").ListAsync("term", "title", "asc", 2, 5, TestContext.Current.CancellationToken);

        var uri = handler.Requests[0].RequestUri!;
        Assert.Equal("/api/v1/notes/records", uri.AbsolutePath);
        Assert.Contains("q=term", uri.Query);
        Assert.Contains("sort=title", uri.Query);
        Assert.Contains("page=2", uri.Query);
        Assert.Equal("Bearer static-token", handler.Requests[0].Headers.Authorization!.ToString());
        Assert.Equal("x", page.Rows[0].Data.GetProperty("title").GetString());
    }

    [Fact]
    public async Task An_upload_posts_multipart_to_the_bucket_it_names()
    {
        var (client, handler) = Build();
        client.UseApiToken("static-token");
        handler.Reply(HttpStatusCode.Created,
            """{"id":"avatars/f1.png","bucket":"avatars","name":"f1.png","url":"http://localhost:5299/uploads/avatars/f1.png","size":3,"content_type":"image/png"}""");

        using var content = new MemoryStream([1, 2, 3]);
        var stored = await client.Storage.Bucket("avatars")
            .UploadAsync("face.png", "image/png", content, TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/files/avatars", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.StartsWith("multipart/form-data", handler.Requests[0].Content!.Headers.ContentType!.MediaType);
        Assert.Equal("avatars/f1.png", stored.Id);
        Assert.EndsWith("/uploads/avatars/f1.png", stored.Url);
    }

    [Fact]
    public async Task Deleting_a_file_addresses_it_by_name_inside_its_bucket()
    {
        var (client, handler) = Build();
        client.UseApiToken("static-token");
        handler.Reply(HttpStatusCode.OK, """{"deleted":"avatars/f1.png"}""");

        await client.Storage.Bucket("avatars").DeleteAsync("avatars/f1.png", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/files/avatars/f1.png", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
    }
}
