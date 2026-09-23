namespace TimeBlocker.Service.Blocking.Dns;

/// <summary>
/// DNS 메시지에서 필요한 최소한만 다룬다.
/// 완전한 DNS 구현이 아니라 "질의 이름을 읽고, 차단이면 NXDOMAIN 을 만든다" 용도다.
/// 나머지 질의는 손대지 않고 그대로 상위 DNS 로 전달한다.
/// </summary>
public static class DnsMessage
{
    private const int HeaderLength = 12;

    /// <summary>
    /// 질의 이름(QNAME)을 읽는다. 실패하면 null.
    /// 압축 포인터는 질의 섹션에 나오지 않으므로 처리하지 않는다.
    /// </summary>
    public static string? TryReadQuestionName(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderLength + 1) return null;

        var questionCount = (message[4] << 8) | message[5];
        if (questionCount < 1) return null;

        var offset = HeaderLength;
        var labels = new List<string>();

        while (offset < message.Length)
        {
            int length = message[offset];

            if (length == 0) break;                 // 이름 끝
            if ((length & 0xC0) == 0xC0) return null; // 압축 포인터 - 질의에는 없어야 한다
            if (length > 63) return null;

            offset++;
            if (offset + length > message.Length) return null;

            labels.Add(System.Text.Encoding.ASCII.GetString(message.Slice(offset, length)));
            offset += length;

            if (labels.Count > 128) return null; // 비정상 패킷 방어
        }

        return labels.Count == 0 ? null : string.Join('.', labels).ToLowerInvariant();
    }

    /// <summary>
    /// 같은 ID/질의를 유지한 채 RCODE=NXDOMAIN(3) 응답을 만든다.
    /// 답변 레코드는 넣지 않는다.
    /// </summary>
    public static byte[]? TryBuildNxDomainResponse(ReadOnlySpan<byte> query)
    {
        if (query.Length < HeaderLength) return null;

        // 질의 섹션의 끝 위치를 찾는다. (이름 + QTYPE 2 + QCLASS 2)
        var offset = HeaderLength;
        while (offset < query.Length)
        {
            int length = query[offset];
            if (length == 0)
            {
                offset++;
                break;
            }
            if ((length & 0xC0) == 0xC0) return null;

            offset += length + 1;
        }

        offset += 4;
        if (offset > query.Length) return null;

        var response = new byte[offset];
        query[..offset].CopyTo(response);

        // Flags: QR=1(응답), Opcode 유지, AA=0, TC=0, RD 유지, RA=1, RCODE=3(NXDOMAIN)
        var recursionDesired = (query[2] & 0x01) != 0;
        response[2] = (byte)(0x80 | (query[2] & 0x78) | (recursionDesired ? 0x01 : 0x00));
        response[3] = 0x83; // RA=1, RCODE=3

        // ANCOUNT / NSCOUNT / ARCOUNT = 0
        response[6] = 0; response[7] = 0;
        response[8] = 0; response[9] = 0;
        response[10] = 0; response[11] = 0;

        return response;
    }

    /// <summary>
    /// 상위 DNS 가 죽었을 때 돌려줄 SERVFAIL(2) 응답.
    /// 클라이언트가 응답 없이 오래 기다리지 않도록 한다.
    /// </summary>
    public static byte[]? TryBuildServerFailureResponse(ReadOnlySpan<byte> query)
    {
        var response = TryBuildNxDomainResponse(query);
        if (response is null) return null;

        response[3] = 0x82; // RA=1, RCODE=2 (SERVFAIL)
        return response;
    }

    /// <summary>
    /// 질의 이름이 차단 도메인 하나에 해당하는지 판정하는 규칙.
    ///
    ///   questionName == blockedDomain
    ///   OR questionName.EndsWith("." + blockedDomain)
    ///
    /// 부분 문자열 검색(Contains)은 절대 쓰지 않는다.
    /// 그렇게 하면 notgooglevideo.com / myyoutube.com 처럼 관계없는 도메인까지 막힌다.
    ///
    /// 예) blockedDomain = googlevideo.com
    ///     googlevideo.com                 -> true  (정확히 일치)
    ///     rr1---sn-xxxx.googlevideo.com   -> true  (하위 도메인)
    ///     abc.googlevideo.com             -> true  (하위 도메인)
    ///     notgooglevideo.com              -> false (앞에 점이 없다)
    /// </summary>
    public static bool Matches(string questionName, string blockedDomain)
    {
        if (string.IsNullOrEmpty(questionName) || string.IsNullOrEmpty(blockedDomain)) return false;

        var name = Normalize(questionName);
        var blocked = Normalize(blockedDomain);
        if (name.Length == 0 || blocked.Length == 0) return false;

        return string.Equals(name, blocked, StringComparison.Ordinal)
               || name.EndsWith("." + blocked, StringComparison.Ordinal);
    }

    /// <summary>
    /// 차단 목록 전체와 질의 이름을 비교한다. 판정 규칙은 <see cref="Matches"/> 와 동일하다.
    ///
    /// 구현은 차단 목록을 하나씩 훑는 대신 질의 이름의 상위 도메인을 차례로 만들어
    /// 해시 집합에서 찾는다. (질의당 도메인 개수가 아니라 라벨 개수에만 비례한다)
    /// 여기서 쓰는 Contains 는 "집합에 이 키가 있는가" 이며 부분 문자열 검색이 아니다.
    /// </summary>
    public static bool IsBlocked(string questionName, IReadOnlySet<string> blockedDomains)
    {
        if (blockedDomains.Count == 0) return false;

        var name = Normalize(questionName);
        if (name.Length == 0) return false;

        // 1) 정확히 일치
        if (blockedDomains.Contains(name)) return true;

        // 2) 하위 도메인: 점 뒤의 상위 도메인을 차례로 확인한다.
        //    a.b.googlevideo.com -> b.googlevideo.com -> googlevideo.com
        //    이는 name.EndsWith("." + blocked) 와 같은 의미다.
        var index = name.IndexOf('.');
        while (index >= 0 && index + 1 < name.Length)
        {
            var parent = name[(index + 1)..];
            if (blockedDomains.Contains(parent)) return true;

            index = name.IndexOf('.', index + 1);
        }

        return false;
    }

    /// <summary>비교 전에 대소문자와 끝점(FQDN 표기)을 통일한다.</summary>
    public static string Normalize(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();
}
