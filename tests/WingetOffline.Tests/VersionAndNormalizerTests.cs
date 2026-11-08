using WingetOffline.Core.Matching;
using WingetOffline.Core.Versioning;

namespace WingetOffline.Tests;

public class VersionComparerTests
{
    private static int C(string a, string b) => WingetVersionComparer.Instance.Compare(a, b);

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.9", "1.10")]
    [InlineData("2026.3.2", "2026.3.3")]
    [InlineData("1.0.0-beta", "1.0.0")]
    [InlineData("1.0.0-beta", "1.0.0-rc")]
    [InlineData("", "1")]
    [InlineData("25+37", "25.0.1+11")]            // + = رقم البناء: يُقارَن بعد النواة فقط (BellSoft.LibericaJRE.25)
    [InlineData("25.0.1+11", "25.0.1+12")]
    [InlineData("0.1.0-draft42", "0.1.0-draft-43")]  // الرقم الملتصق بالكلمة يُفصل (Andromeda)
    [InlineData("1.0", "1.0+1")]
    [InlineData("1.2.3a", "1.2.3")]                // قرار موثَّق: اللاحقة الحرفية = ما قبل الإصدار
    public void Left_is_older(string older, string newer)
    {
        Assert.True(C(older, newer) < 0);
        Assert.True(C(newer, older) > 0);
    }

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("v1.2", "1.2")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("M138.0.7204.300", "138.0.7204.300")]  // بادئة حرف واحد تُتجاهل (Thorium)
    [InlineData("1.0+5", "1.0+5")]
    [InlineData("2", "2.0.0.0")]
    public void Versions_are_equal(string a, string b) => Assert.Equal(0, C(a, b));
}

public class NormalizerTests
{
    [Theory]
    [InlineData("7-Zip 26.02 (x64)", "7 zip")]
    [InlineData("Microsoft Visual Studio Code (User)", "microsoft visual studio code")]
    [InlineData("Python 3.12.1 (64-bit)", "python")]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("Mozilla Firefox (x64 en-US)", "mozilla firefox")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "microsoft visual c 2015 2022 redistributable")]
    public void Name_is_normalised(string input, string expected) => Assert.Equal(expected, Normalizer.Name(input));

    [Theory]
    [InlineData("Microsoft Corporation", "microsoft")]
    [InlineData("Igor Pavlov", "igor pavlov")]
    [InlineData("AgileBits, Inc.", "agilebits")]
    public void Publisher_is_normalised(string input, string expected) => Assert.Equal(expected, Normalizer.Publisher(input));

    [Theory]
    [InlineData("powertoys preview", "powertoys")]
    [InlineData("foo beta preview", "foo")]
    [InlineData("windows terminal preview", "windows terminal")]
    [InlineData("preview", "preview")]          // كلمة واحدة: لا تُحذف
    [InlineData("dev home", "dev home")]        // لا تُحذف إلا الوسوم الختامية
    [InlineData("", "")]
    public void Channel_suffixes_are_stripped(string input, string expected) =>
        Assert.Equal(expected, Normalizer.StripQualifiers(input));

    [Fact]
    public void Publisher_similarity()
    {
        Assert.True(Normalizer.PublisherSimilar("microsoft", "microsoft"));
        Assert.True(Normalizer.PublisherSimilar("microsoft", "microsoft azure"));
        Assert.False(Normalizer.PublisherSimilar("igor pavlov", "microsoft"));
        Assert.False(Normalizer.PublisherSimilar("", "microsoft"));
    }

    [Theory]
    [InlineData("ad 9.8.1", "9.8.1")]
    [InlineData("4.21 Final-1 [Build #2382]", "4.21")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("", "")]
    public void Comparable_version(string input, string expected) => Assert.Equal(expected, Normalizer.ComparableVersion(input));

    [Fact]
    public void Jaccard_similarity()
    {
        var a = Normalizer.Tokens("vmware workstation");
        var b = Normalizer.Tokens("vmware workstation pro");
        Assert.Equal(2.0 / 3.0, Normalizer.Jaccard(a, b), 5);
        Assert.Equal(1.0, Normalizer.Jaccard(a, a), 5);
        Assert.Equal(0.0, Normalizer.Jaccard(a, Normalizer.Tokens("other tool")), 5);
    }
}
