using System.Collections;
using Avro;
using Avro.Generic;

namespace Shared.Avro;

/// <summary>
/// Turns an event record (a C# object) into the Avro generic record its schema describes, field by field by name. The
/// logical types of the schemas are mapped by hand so that the result is deterministic and nothing is silently rounded:
/// a Guid is a <c>uuid</c> string, a time is epoch milliseconds, money is a scaled integer in two's complement bytes.
/// </summary>
public static class AvroRecordMapper
{
    public static GenericRecord ToGenericRecord(RecordSchema schema, object payload)
    {
        var record = new GenericRecord(schema);
        var type = payload.GetType();
        foreach (var field in schema.Fields)
        {
            var property = type.GetProperty(field.Name)
                ?? throw new InvalidOperationException($"{type.Name} has no property '{field.Name}' that the schema {schema.Fullname} requires");
            record.Add(field.Name, Convert(property.GetValue(payload), field.Schema, $"{schema.Name}.{field.Name}"));
        }

        return record;
    }

    private static object? Convert(object? value, Schema schema, string where)
    {
        if (schema is UnionSchema union)
        {
            if (value is null)
            {
                return union.Schemas.Any(s => s.Tag == Schema.Type.Null)
                    ? null
                    : throw new InvalidOperationException($"{where} is null but its schema does not allow null");
            }

            var branch = union.Schemas.First(s => s.Tag != Schema.Type.Null);
            return Convert(value, branch, where);
        }

        if (value is null)
        {
            throw new InvalidOperationException($"{where} is null but its schema does not allow null");
        }

        // The Avro writer applies the schema's logical types itself and expects their natural CLR values (Guid,
        // DateTime, AvroDecimal); this only adapts what the producer's records hold to those, refusing rather than
        // rounding.
        switch (schema)
        {
            case LogicalSchema { LogicalType.Name: "uuid" }:
                return value is Guid g ? g : Guid.Parse((string)value);

            case LogicalSchema { LogicalType.Name: "timestamp-millis" }:
                return value switch
                {
                    DateTime dt => dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                    DateTimeOffset dto => dto.UtcDateTime,
                    _ => throw new InvalidOperationException($"{where}: {value.GetType().Name} is not a time"),
                };

            case LogicalSchema { LogicalType.Name: "decimal" } decimalSchema:
            {
                var scale = int.Parse(decimalSchema.GetProperty("scale") ?? "0");
                var factor = Pow10(scale);
                var scaled = (decimal)value * factor;
                if (scaled != decimal.Truncate(scaled))
                {
                    throw new InvalidOperationException($"{where}: {value} has more than {scale} decimal places");
                }

                // Built with exactly the schema's scale: a value written as 1234.5 and one written as 1234.50 must
                // be the same bytes.
                var unscaled = (long)scaled;
                var magnitude = (ulong)Math.Abs(unscaled);
                return new AvroDecimal(new decimal((int)(magnitude & 0xFFFFFFFF), (int)(magnitude >> 32), 0, unscaled < 0, (byte)scale));
            }

            case ArraySchema arraySchema:
                return ((IEnumerable)value).Cast<object?>().Select(i => Convert(i, arraySchema.ItemSchema, where)).ToArray();

            case PrimitiveSchema when schema.Tag == Schema.Type.String:
                return value is Guid guid ? guid.ToString("D") : (string)value;

            default:
                return value;
        }
    }

    private static decimal Pow10(int scale)
    {
        var result = 1m;
        for (var i = 0; i < scale; i++)
        {
            result *= 10;
        }

        return result;
    }
}
