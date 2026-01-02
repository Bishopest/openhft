using System;
using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;

namespace OpenHFT.Core.Utils;

public static class FastJsonParser
{
    /// <summary>
    /// 할당 없이 JsonElement(Utf8JsonReader)에서 decimal을 파싱합니다.
    /// </summary>
    public static bool TryParseDecimal(ref Utf8JsonReader reader, out decimal value)
    {
        // 1. 데이터가 숫자로 들어온 경우 (예: 123.45)
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetDecimal(out value);
        }

        // 2. 데이터가 문자열로 들어온 경우 (예: "123.45")
        if (reader.TokenType == JsonTokenType.String)
        {
            // [중요] Utf8JsonReader.ValueSpan은 문자열의 경우 따옴표가 이미 제거된 상태입니다.

            if (!reader.HasValueSequence)
            {
                // 케이스 A: 데이터가 단일 버퍼에 있는 경우 (가장 흔함 - 할당 0)
                ReadOnlySpan<byte> span = reader.ValueSpan;
                return Utf8Parser.TryParse(span, out value, out int consumed) && consumed == span.Length;
            }
            else
            {
                // 케이스 B: 데이터가 여러 패킷(Sequence)으로 쪼개져 있는 경우
                int length = (int)reader.ValueSequence.Length;

                if (length <= 128) // 가격 문자열은 보통 128바이트를 넘지 않음
                {
                    // 스택에 임시 버퍼 생성
                    Span<byte> stackBuffer = stackalloc byte[length];
                    // 쪼개진 데이터를 스택 버퍼로 복사 (할당 0)
                    reader.ValueSequence.CopyTo(stackBuffer);

                    // 스택 버퍼 범위 내에서 즉시 파싱하여 수명 문제 해결
                    return Utf8Parser.TryParse(stackBuffer, out value, out int consumed) && consumed == length;
                }
                else
                {
                    // 예외 케이스: 가격이 128바이트를 넘는 경우 (사실상 불가능하지만 안전을 위해)
                    return decimal.TryParse(reader.GetString(), out value);
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 문자열 할당 없이 프로퍼티 이름을 비교합니다.
    /// </summary>
    public static bool IsProperty(ref Utf8JsonReader reader, ReadOnlySpan<byte> propertyNameU8)
    {
        return reader.ValueTextEquals(propertyNameU8);
    }
}