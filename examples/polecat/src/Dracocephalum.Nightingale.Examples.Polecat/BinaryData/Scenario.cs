using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;

namespace Dracocephalum.Nightingale.Examples.Polecat.BinaryData;

/// <summary>
/// An event's second body, against a Nightingale server, step by step: personal data encrypted
/// under a key of its own subject and stored as binary data beside JSON that stays readable, read
/// back and decrypted, then forgotten by destroying the key while the JSON still says what
/// happened; an event of binary data alone; the same bytes through the category stream; and a
/// content type without binary data refused. The program prints the steps; the test project
/// asserts the report.
/// </summary>
public static class Scenario
{
    /// <summary>The media type the sample gives its encrypted data: its own, since any may be named.</summary>
    public const string EncryptedContentType = "application/vnd.nightingale-sample.aes-gcm";

    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Stream">The stream the run used.</param>
    /// <param name="ContentType">The binary data's content type read back from the first event's metadata.</param>
    /// <param name="Country">The readable part of the first event, from its JSON.</param>
    /// <param name="DecryptedName">The name decrypted from the first event's binary data while its key existed.</param>
    /// <param name="BinaryOnlyJson">The JSON of the event appended with binary data only, as read back.</param>
    /// <param name="BinaryOnlyLength">How many bytes of binary data that event read back with.</param>
    /// <param name="CountryAfterShredding">The first event's JSON, read again once the key was destroyed.</param>
    /// <param name="DecryptableAfterShredding">Whether the first event's binary data could still be decrypted once the key was destroyed.</param>
    /// <param name="CategoryWithBinaryData">How many events of the category stream carried binary data.</param>
    /// <param name="ContentTypeAloneRefusal">The exception an event with a content type and no binary data was refused with.</param>
    public sealed record Report(
        string Stream,
        string? ContentType,
        string? Country,
        string DecryptedName,
        string BinaryOnlyJson,
        int BinaryOnlyLength,
        string? CountryAfterShredding,
        bool DecryptableAfterShredding,
        int CategoryWithBinaryData,
        string ContentTypeAloneRefusal);

    /// <summary>Runs the scenario.</summary>
    /// <param name="run">The run the scenario is part of: its database, its server, and a tenant for the scenario.</param>
    /// <param name="output">Where the steps are narrated.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public static async Task<Report> RunAsync(ExampleRun run, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(output);

        var tenant = await run.CreateTenantAsync("binary-data", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"1. Created the tenant {tenant.Name} for this scenario; its streams live there and nowhere else.").ConfigureAwait(false);

        await using var connection = run.Server.Connect(tenant);
        var client = connection.Client;
        const string stream = "people-1";

        // The keys live outside the store, one per person; destroying one forgets that person.
        var keys = new Dictionary<string, byte[]> { ["person-1"] = RandomNumberGenerator.GetBytes(32) };
        var encrypted = Encrypt(keys["person-1"], "{\"name\":\"Sample Person\",\"email\":\"person@example.invalid\"}");
        var registered = new EventData(
            Guid.NewGuid(),
            "person_registered",
            Encoding.UTF8.GetBytes("{\"personId\":\"person-1\",\"country\":\"NZ\"}"),
            new JsonObject { [MetadataKeys.BinaryContentType] = EncryptedContentType },
            encrypted);
        byte[] thumbnail = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF];
        var thumbnailAdded = new EventData(Guid.NewGuid(), "thumbnail_added", default, null, thumbnail);
        var appended = await client.AppendToStreamAsync(stream, StreamState.NoStream, [registered, thumbnailAdded], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"2. Appended to {stream} a registration whose personal data is encrypted under the person's own key, {encrypted.Length} bytes of binary data beside readable JSON, and a thumbnail of {thumbnail.Length} bytes as binary data alone.").ConfigureAwait(false);

        var events = await ReadAsync(client, stream, cancellationToken).ConfigureAwait(false);
        var contentType = events[0].Metadata[MetadataKeys.BinaryContentType]?.GetValue<string>();
        var country = ReadCountry(events[0]);
        var decrypted = JsonNode.Parse(Decrypt(keys["person-1"], events[0].BinaryData.ToArray()))?["name"]?.GetValue<string>() ?? string.Empty;
        await output.WriteLineAsync($"3. Read it back: the registration's JSON says country {country}, its binary data is {contentType}, and with the key it decrypts to the name {decrypted}.").ConfigureAwait(false);

        var binaryOnlyJson = Encoding.UTF8.GetString(events[1].Data.Span);
        await output.WriteLineAsync($"4. The thumbnail reads back with {events[1].BinaryData.Length} bytes of binary data and the JSON {binaryOnlyJson}, the placeholder an event of binary data alone stores.").ConfigureAwait(false);

        keys.Remove("person-1");
        var afterShredding = await ReadAsync(client, stream, cancellationToken).ConfigureAwait(false);
        var countryAfter = ReadCountry(afterShredding[0]);
        var decryptable = keys.ContainsKey("person-1");
        await output.WriteLineAsync($"5. Destroyed the person's key: the store still holds the event and its JSON still says country {countryAfter}, but nothing can decrypt the personal data any more, though nothing was deleted.").ConfigureAwait(false);

        await ExampleMark.WaitUntilCoversAsync(client, appended.Position, cancellationToken).ConfigureAwait(false);
        var category = await ReadAsync(client, "$ce-people", cancellationToken).ConfigureAwait(false);
        var withBinaryData = category.Count(e => !e.BinaryData.IsEmpty);
        await output.WriteLineAsync($"6. Read $ce-people once the store's mark covered the append: {withBinaryData} of its {category.Count} events carry binary data, the same bytes as the stream's.").ConfigureAwait(false);

        string refusal;
        try
        {
            var unlabelled = new EventData(Guid.NewGuid(), "person_noted", Encoding.UTF8.GetBytes("{}"), new JsonObject { [MetadataKeys.BinaryContentType] = EncryptedContentType });
            await client.AppendToStreamAsync(stream, StreamState.Any, [unlabelled], cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A content type without binary data was accepted.");
        }
        catch (ArgumentException refused)
        {
            refusal = refused.GetType().Name;
            await output.WriteLineAsync($"7. An event naming a content type for binary data it does not carry was refused with {refusal}: {refused.Message}").ConfigureAwait(false);
        }

        return new Report(stream, contentType, country, decrypted, binaryOnlyJson, events[1].BinaryData.Length, countryAfter, decryptable, withBinaryData, refusal);
    }

    private static string? ReadCountry(EventRecord record) =>
        JsonNode.Parse(record.Data.Span)?["country"]?.GetValue<string>();

    private static async Task<IReadOnlyList<EventRecord>> ReadAsync(NightingaleClient client, string stream, CancellationToken cancellationToken)
    {
        var events = new List<EventRecord>();
        await using var read = client.ReadStreamAsync(Direction.Forwards, stream, StreamPosition.Start, cancellationToken: cancellationToken);
        await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            events.Add(record);
        }

        return events;
    }

    /// <summary>AES-GCM: the nonce, then the tag, then the ciphertext.</summary>
    private static byte[] Encrypt(byte[] key, string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var sealedData = new byte[AesGcm.NonceByteSizes.MaxSize + AesGcm.TagByteSizes.MaxSize + plain.Length];
        var nonce = sealedData.AsSpan(0, AesGcm.NonceByteSizes.MaxSize);
        var tag = sealedData.AsSpan(nonce.Length, AesGcm.TagByteSizes.MaxSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plain, sealedData.AsSpan(nonce.Length + tag.Length), tag);
        return sealedData;
    }

    private static string Decrypt(byte[] key, byte[] sealedData)
    {
        var nonce = sealedData.AsSpan(0, AesGcm.NonceByteSizes.MaxSize);
        var tag = sealedData.AsSpan(nonce.Length, AesGcm.TagByteSizes.MaxSize);
        var cipher = sealedData.AsSpan(nonce.Length + tag.Length);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
