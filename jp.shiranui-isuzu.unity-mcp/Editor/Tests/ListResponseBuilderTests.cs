using System.Collections.Generic;

using NUnit.Framework;
using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Unit tests for <see cref="ListResponseBuilder"/> covering
    /// limit/offset/fields/truncated/next handling per R5.1–R5.2.
    /// </summary>
    [TestFixture]
    internal sealed class ListResponseBuilderTests
    {
        private static IReadOnlyList<JObject> MakeItems(int n)
        {
            var list = new List<JObject>(n);
            for (var i = 0; i < n; i++)
            {
                list.Add(new JObject
                {
                    ["t"] = $"item-{i}",
                    ["m"] = i,
                    ["f"] = i % 2 == 0
                });
            }
            return list;
        }

        [Test]
        public void Build_WithinLimit_ReturnsAllNotTruncated()
        {
            var items = MakeItems(5);

            var result = ListResponseBuilder.Build(items, offset: 0, limit: 10, projector: x => x);

            Assert.AreEqual(5, ((JArray)result["items"]).Count);
            Assert.IsFalse(result["truncated"].Value<bool>());
            Assert.AreEqual(JTokenType.Null, result["next"].Type);
        }

        [Test]
        public void Build_WithLimitSmallerThanTotal_TruncatesAndEmitsNext()
        {
            var items = MakeItems(25);

            var result = ListResponseBuilder.Build(items, offset: 0, limit: 10, projector: x => x);

            Assert.AreEqual(10, ((JArray)result["items"]).Count);
            Assert.IsTrue(result["truncated"].Value<bool>());
            Assert.IsNotNull(result["next"]);
            Assert.AreEqual(10, result["next"]["offset"].Value<int>());
            Assert.AreEqual(10, result["next"]["limit"].Value<int>());
        }

        [Test]
        public void Build_WithOffset_SkipsLeadingItems()
        {
            var items = MakeItems(10);

            var result = ListResponseBuilder.Build(items, offset: 3, limit: 2, projector: x => x);

            var arr = (JArray)result["items"];
            Assert.AreEqual(2, arr.Count);
            Assert.AreEqual("item-3", arr[0]["t"].ToString());
            Assert.AreEqual("item-4", arr[1]["t"].ToString());
            Assert.IsTrue(result["truncated"].Value<bool>());
            Assert.AreEqual(5, result["next"]["offset"].Value<int>());
        }

        [Test]
        public void Build_WithFieldsFilter_KeepsAllowedKeysOnly()
        {
            var items = MakeItems(3);
            var filter = new[] { "t", "f" };

            var result = ListResponseBuilder.Build(items, 0, 10, x => x, filter);

            var first = (JObject)((JArray)result["items"])[0];
            Assert.IsTrue(first.ContainsKey("t"));
            Assert.IsTrue(first.ContainsKey("f"));
            Assert.IsFalse(first.ContainsKey("m"), "m should have been filtered out");
        }

        [Test]
        public void Build_EmptyInput_ReturnsEmptyNotTruncated()
        {
            var items = new List<JObject>();

            var result = ListResponseBuilder.Build(items, 0, 10, x => x);

            Assert.AreEqual(0, ((JArray)result["items"]).Count);
            Assert.IsFalse(result["truncated"].Value<bool>());
            Assert.AreEqual(JTokenType.Null, result["next"].Type);
        }

        [Test]
        public void Build_LimitZero_IsTreatedAsUnlimited()
        {
            var items = MakeItems(4);

            var result = ListResponseBuilder.Build(items, offset: 0, limit: 0, projector: x => x);

            Assert.AreEqual(4, ((JArray)result["items"]).Count);
            Assert.IsFalse(result["truncated"].Value<bool>());
        }

        [Test]
        public void Build_OffsetBeyondTotal_ReturnsEmpty()
        {
            var items = MakeItems(3);

            var result = ListResponseBuilder.Build(items, offset: 99, limit: 10, projector: x => x);

            Assert.AreEqual(0, ((JArray)result["items"]).Count);
            Assert.IsFalse(result["truncated"].Value<bool>());
        }

        [Test]
        public void ParseFieldsParam_HandlesNullAndWhitespaceAsNoFilter()
        {
            Assert.IsNull(ListResponseBuilder.ParseFieldsParam(null));
            Assert.IsNull(ListResponseBuilder.ParseFieldsParam(""));
            Assert.IsNull(ListResponseBuilder.ParseFieldsParam("   "));
        }

        [Test]
        public void ParseFieldsParam_SplitsAndTrims()
        {
            var parsed = ListResponseBuilder.ParseFieldsParam("t, m ,f");
            CollectionAssert.AreEqual(new[] { "t", "m", "f" }, parsed);
        }
    }
}
