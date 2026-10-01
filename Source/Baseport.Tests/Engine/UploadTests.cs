using Xunit;
using Baseport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Baseport.Tests;

public class UploadTests
{
    [Fact]
    public async Task UploadWrittenOnSave()
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
    [InlineData("GET", "GET,POST,DELETE", "POST", false)]
    [InlineData("GET", "GET,POST,DELETE", "DELETE", false)]
    [InlineData("POST", "GET,POST,DELETE", "GET", false)]
    [InlineData("GET,POST,DELETE", "GET,POST,DELETE", "POST", true)]
    [InlineData("GET,POST,DELETE", "GET", "POST", false)]
    [InlineData("GET,POST,DELETE", "GET,POST", "DELETE", false)]
    [InlineData("GET,POST,PATCH,PUT,DELETE", "GET", "get", true)]
    public void StorageNeedsBucketAndKeyMethod(string keyMethods, string bucketMethods, string method, bool allowed)
    {
        var bucket = new Bucket { ApiMethods = bucketMethods };
        Assert.Equal(allowed, BucketMethods.Allows(bucket, new UserAccount { ApiTokenMethods = keyMethods }, method));
    }
}
