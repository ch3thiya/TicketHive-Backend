using System;
using System.Security.Cryptography;
using System.Text;

namespace Booking.Service.Services;

public class TicketCodeGenerator : ITicketCodeGenerator
{
    private static readonly char[] Base32Chars = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ".ToCharArray();

    public string GenerateCode()
    {
        // 12 characters from un-ambiguous Base32 charset (excluding 0, 1, O, I to avoid visual confusion)
        var bytes = new byte[12];
        RandomNumberGenerator.Fill(bytes);

        var result = new StringBuilder(14);
        result.Append("TKT-");
        for (int i = 0; i < 12; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                result.Append('-');
            }
            var index = bytes[i] % Base32Chars.Length;
            result.Append(Base32Chars[index]);
        }

        return result.ToString();
    }
}
