namespace FSharp.AWS.DynamoDB.Tests

open System
open System.IO

open Swensen.Unquote
open Xunit

open FSharp.AWS.DynamoDB

// Reproduces https://github.com/jet/equinox/issues/482.
//
// Root cause: Amazon.Runtime.Internal.Util.StringUtils.FromMemoryStream (AWSSDK.Core v4) is used by
// the DynamoDB request marshaller to base64-encode every AttributeValue.B. When the MemoryStream does
// not have a publicly-visible buffer - which is the case for every MemoryStream this library hands to
// AttributeValue.B, since all of them are built via `new MemoryStream(bytes, writable = false)` - it
// falls back to:
//
//     byte[] array = ArrayPool<byte>.Shared.Rent((int)value.Length);
//     value.Read(array, 0, (int)value.Length);              // return value never checked
//     return Convert.ToBase64String(array, 0, (int)value.Length);
//
// If the stream's Position is not 0 when this runs, Read only fills part of `array`, and the
// remainder - which may hold bytes left over from a *different*, unrelated caller's earlier
// Rent/Return of the same pooled array - is silently included in the base64 output as if it were
// real event data. FSharp.AWS.DynamoDB never advances Position on these streams itself, but it also
// never resets it or takes a defensive copy, so any caller who reads a few bytes from (or otherwise
// reuses) a stream before handing it to this library will silently corrupt the write.
//
// This test demonstrates the underlying defect directly and deterministically. In real event-sourced
// traffic (Equinox.DynamoStore) it instead shows up sporadically, and only on requests carrying many
// (100s of) binary attributes in one large multi-item Put after a lot of prior small ArrayPool churn
// on the same process - that variant requires sustained warm-up traffic and was not reliable enough
// to use as a fast, deterministic regression test.

[<AutoOpen>]
module BinaryPositionTypes =

    type Rec =
        { [<HashKey>]
          HashKey: string
          [<RangeKey>]
          RangeKey: int64
          Data: MemoryStream option }

type ``Binary attribute Position handling tests``(fixture: TableFixture) =

    let table = fixture.CreateEmpty<Rec>()

    [<Fact>]
    let ``Put round-trips a MemoryStream whose Position is not 0`` () = async {
        let content =
            Text.Encoding.UTF8.GetBytes "HELLO-WORLD-PAYLOAD-0123456789-PADDING-TO-MAKE-IT-A-BIT-LONGER-1234567890"

        let stream = new MemoryStream(content, writable = false)
        stream.Position <- 10L // simulates any caller who has read from (or rewound) the stream before this library uses it

        let item = { HashKey = guid (); RangeKey = 0L; Data = Some stream }
        do! table.PutItemAsync item |> Async.Ignore

        let! stored = table.GetItemAsync(table.Template.ExtractKey item)
        let actual = stored.Data |> Option.map (fun s -> s.ToArray())

        actual =! Some content
    }

    interface IClassFixture<TableFixture>
