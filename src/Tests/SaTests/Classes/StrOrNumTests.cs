using Sa.Classes;
using Sa.Extensions;

namespace SaTests.Classes;

public class StrOrNumTests
{
    [Fact]
    public void StrOrNum_Mustbe_Serialize_json()
    {
        StrOrNum expected = "{Hi}\"";

        string json = expected.ToJson();

        Assert.NotEmpty(json);

        StrOrNum actual = json.FromJson<StrOrNum>()!;

        Assert.Equal(expected, actual);


        expected = 123;

        json = expected.ToJson();

        Assert.NotEmpty(json);

        actual = json.FromJson<StrOrNum>()!;

        Assert.Equal(expected, actual);
    }
}