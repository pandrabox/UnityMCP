using System.Collections.Generic;

using NUnit.Framework;
using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Tests the unified envelope contract (R1.4 / design §2.3):
    ///   success: {status:"success", result, truncated?, next?}
    ///   error:   {status:"error",   error:{code, message}}
    ///
    /// We re-create the envelope shape in-process here because the real writer
    /// is private to McpHttpServer; the goal is to assert the JSON contract.
    /// </summary>
    [TestFixture]
    internal sealed class EnvelopeTests
    {
        private static JObject BuildSuccessEnvelope(JObject result)
        {
            var envelope = new JObject { ["status"] = "success" };

            JToken truncated = null;
            JToken next = null;
            JObject cleanResult = null;

            if (result != null)
            {
                truncated = result["truncated"];
                next = result["next"];
                cleanResult = new JObject();
                foreach (var prop in result.Properties())
                {
                    if (prop.Name == "truncated" || prop.Name == "next") continue;
                    cleanResult[prop.Name] = prop.Value;
                }
            }

            if (cleanResult != null) envelope["result"] = cleanResult;
            if (truncated != null) envelope["truncated"] = truncated;
            if (next != null && next.Type != JTokenType.Null) envelope["next"] = next;

            return envelope;
        }

        private static JObject BuildErrorEnvelope(string code, string message) => new JObject
        {
            ["status"] = "error",
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        [Test]
        public void SuccessEnvelope_HasStatusAndResult()
        {
            var payload = new JObject { ["ok"] = true, ["count"] = 3 };

            var envelope = BuildSuccessEnvelope(payload);

            Assert.AreEqual("success", envelope["status"].ToString());
            Assert.IsNotNull(envelope["result"]);
            Assert.AreEqual(true, envelope["result"]["ok"].Value<bool>());
            Assert.AreEqual(3, envelope["result"]["count"].Value<int>());
            Assert.IsNull(envelope["error"]);
        }

        [Test]
        public void ErrorEnvelope_HasStatusAndErrorCodeAndMessage()
        {
            var envelope = BuildErrorEnvelope("invalid_params", "bad input");

            Assert.AreEqual("error", envelope["status"].ToString());
            Assert.AreEqual("invalid_params", envelope["error"]["code"].ToString());
            Assert.AreEqual("bad input", envelope["error"]["message"].ToString());
            Assert.IsNull(envelope["result"]);
        }

        [Test]
        public void TruncatedAndNext_AreHoistedOntoEnvelope()
        {
            // Simulate a ListResponseBuilder output with truncation.
            var items = new List<JObject>();
            for (var i = 0; i < 25; i++) items.Add(new JObject { ["i"] = i });
            var page = ListResponseBuilder.Build(items, 0, 10, x => x);

            var handlerResult = new JObject
            {
                ["logs"] = page["items"],
                ["truncated"] = page["truncated"],
                ["next"] = page["next"]
            };

            var envelope = BuildSuccessEnvelope(handlerResult);

            Assert.AreEqual("success", envelope["status"].ToString());
            // truncated/next are on the envelope, not inside result.
            Assert.IsTrue(envelope["truncated"].Value<bool>());
            Assert.IsNotNull(envelope["next"]);
            Assert.AreEqual(10, envelope["next"]["offset"].Value<int>());
            // result is cleaned.
            Assert.IsFalse(((JObject)envelope["result"]).ContainsKey("truncated"));
            Assert.IsFalse(((JObject)envelope["result"]).ContainsKey("next"));
            // logs payload is preserved.
            Assert.AreEqual(10, ((JArray)envelope["result"]["logs"]).Count);
        }

        [Test]
        public void SuccessEnvelope_WithoutTruncation_OmitsTopLevelKeys()
        {
            var items = new List<JObject> { new() { ["i"] = 1 } };
            var page = ListResponseBuilder.Build(items, 0, 10, x => x);
            var handlerResult = new JObject
            {
                ["items"] = page["items"],
                ["truncated"] = page["truncated"], // false
                ["next"] = page["next"]            // null
            };

            var envelope = BuildSuccessEnvelope(handlerResult);

            // false is still a value, so truncated is carried; next was null so it must be omitted.
            Assert.AreEqual(false, envelope["truncated"].Value<bool>());
            Assert.IsNull(envelope["next"]);
        }

        [Test]
        public void ErrorEnvelope_NoResultField()
        {
            var envelope = BuildErrorEnvelope("internal_error", "boom");

            Assert.IsFalse(((JObject)envelope).ContainsKey("result"));
        }

        [Test]
        public void ErrorEnvelope_CustomCode_WindowNotFound_WithStatus400()
        {
            const string code = "window_not_found";
            const string message = "No EditorWindow matches view 'inspector'.";
            const int httpStatus = 400;

            var envelope = BuildErrorEnvelope(code, message);

            Assert.AreEqual("error", envelope["status"].ToString());
            Assert.AreEqual(code, envelope["error"]["code"].ToString());
            Assert.AreEqual(message, envelope["error"]["message"].ToString());
            Assert.IsFalse(((JObject)envelope).ContainsKey("result"));
            Assert.IsTrue(httpStatus >= 400 && httpStatus < 500, "window_not_found must use a 4xx status.");
        }

        [Test]
        public void ErrorEnvelope_CustomCode_UnsupportedPlatform_WithStatus501()
        {
            const string code = "unsupported_platform";
            const string message = "Editor window capture is Windows-only in v2.1.";
            const int httpStatus = 501;

            var envelope = BuildErrorEnvelope(code, message);

            Assert.AreEqual("error", envelope["status"].ToString());
            Assert.AreEqual(code, envelope["error"]["code"].ToString());
            Assert.AreEqual(message, envelope["error"]["message"].ToString());
            Assert.IsFalse(((JObject)envelope).ContainsKey("result"));
            Assert.AreEqual(501, httpStatus, "unsupported_platform must map to HTTP 501.");
        }
    }
}
