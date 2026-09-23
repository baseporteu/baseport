using Xunit;
using Baseport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Baseport.Tests;

public class UploadTests
{
    [Fact]
    public async Task An_uploaded_file_reaches_disk_only_when_the_caller_saves_it()
    {
        FileStore.Initialize("Data Source=baseport.db");
        var bytes = "hello"u8.ToArray();
        using var content = new MemoryStream(bytes);
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "multipart/form-data; boundary=x";
        ctx.Request.Form = new FormCollection(
            new Dictionary<string, StringValues> { ["Note"] = "hi" },
            new FormFileCollection { new FormFile(content, 0, bytes.Length, "Attachment", "note.txt") });
        var fields = new List<FieldDefinition>
        {
            new() { Name = "Note", DataType = "text" },
            new() { Name = "Attachment", DataType = "file" }
        };

        var (obj, errors) = await MultipartRecord.FromRequestAsync(ctx, fields);
        var path = FileStore.Resolve(((string)obj["Attachment"]!).Split("/uploads/")[1])!;
        try
        {
            Assert.Empty(errors);
            Assert.False(File.Exists(path));

            await MultipartRecord.SaveFilesAsync(ctx, obj);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("GET", "POST", false)]
    [InlineData("GET", "DELETE", false)]
    [InlineData("POST", "GET", false)]
    [InlineData("GET,POST,DELETE", "POST", true)]
    public void Storage_honours_the_methods_of_the_key(string keyMethods, string method, bool allowed)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;

        var refusal = StorageEndpoints.KeyGate(new UserAccount { ApiTokenMethods = keyMethods }, ctx);

        Assert.Equal(allowed, refusal is null);
        if (!allowed) Assert.Equal(keyMethods, ctx.Response.Headers.Allow.ToString());
    }
}
