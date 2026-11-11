using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal static class FullProfileChecks
{
    internal static string Fixture(string title="Fixture Auto") => """
        {"remarks":"Fixture Auto","dns":{"queryStrategy":"UseIPv4","servers":["localhost"]},
        "inbounds":[{"tag":"socks","protocol":"socks","listen":"0.0.0.0","port":10808,"settings":{"auth":"noauth"},"sniffing":{"enabled":true,"destOverride":["http","tls"],"routeOnly":false}}],
        "outbounds":[
        {"tag":"t1","protocol":"vless","settings":{"vnext":[{"address":"127.0.0.1","port":9,"users":[{"id":"12345678-1234-4234-8234-123456789abc","encryption":"none"}]}]},"streamSettings":{"network":"tcp","security":"none"}},
        {"tag":"t2","protocol":"vless","settings":{"vnext":[{"address":"127.0.0.1","port":10,"users":[{"id":"12345678-1234-4234-8234-123456789abc","encryption":"none"}]}]},"streamSettings":{"network":"tcp","security":"none"}},
        {"tag":"direct","protocol":"freedom"},{"tag":"block","protocol":"blackhole"}],
        "policy":{"levels":{"0":{"bufferSize":4,"connIdle":180,"handshake":8}}},
        "burstObservatory":{"subjectSelector":["t1","t2"],"pingConfig":{"destination":"https://127.0.0.1:1/","interval":"1h","sampling":2,"timeout":"1s"}},
        "routing":{"domainStrategy":"AsIs","balancers":[{"tag":"Auto","selector":["t1","t2"],"strategy":{"type":"leastLoad","settings":{"expected":1,"maxRTT":"2s","costs":[{"match":"^t1","regexp":true,"value":100},{"match":"^t2","regexp":true,"value":10000}]}}}],
        "rules":[{"type":"field","ip":["geoip:private"],"outboundTag":"direct"},{"type":"field","inboundTag":["socks"],"network":"tcp,udp","balancerTag":"Auto"}]}}
        """.Replace("\"Fixture Auto\"",JsonSerializer.Serialize(title),StringComparison.Ordinal);

    internal static async Task Run(Action<bool,string> check)
    {
        var original=Fixture(); var source=JsonNode.Parse(original)!;
        var imported=ProfileImporter.Parse(original);
        var home=Path.Combine(Path.GetTempPath(),"MorphocyteOS-full-json-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
        var core=new CoreProcessManager();
        try
        {
            var document=ClashConfigDocument.Parse(Path.Combine(home,"profile.yaml"),imported.Yaml);
            using(var plan=XrayBridgePlan.Create(document))
            {
                check(document.ConnectionCount==1 && plan.ProcessPlans.Count==1,"a full automatic profile is one selectable connection in its own Xray process");
                var runtime=JsonNode.Parse(plan.ProcessPlans[0].Configuration)!;
                check(JsonNode.DeepEquals(runtime["dns"],source["dns"]) && JsonNode.DeepEquals(runtime["policy"],source["policy"]),"full-profile DNS and connection policy survive import unchanged");
                check(JsonNode.DeepEquals(runtime["routing"]!["balancers"],source["routing"]!["balancers"])
                    && JsonNode.DeepEquals(runtime["burstObservatory"],source["burstObservatory"]),"leastLoad priorities and continuous connection probing are retained, not flattened to one server");
                check(runtime["outbounds"]!.AsArray().Count==4,"every outbound including direct and block remains in the full profile");
                check(runtime["inbounds"]!.AsArray().Count==1 && runtime["inbounds"]![0]!["listen"]!.ToString()=="127.0.0.1"
                    && runtime["inbounds"]![0]!["settings"]!["auth"]!.ToString()=="password" && runtime["inbounds"]![0]!["port"]!.GetValue<int>()!=10808,
                    "provider listeners are replaced by a random authenticated loopback channel");
                check(runtime["routing"]!["rules"]![1]!["inboundTag"]![0]!.ToString()=="morphocyte-profile-in","provider routing still matches the replacement listener");
                check(runtime["routing"]!["rules"]![0]!["ip"]!.AsArray().Count==18
                    && runtime["routing"]!["rules"]![0]!["ip"]!.AsArray().Any(item=>item!.ToString()=="fc00::/7"),"private address rules retain IPv4 and IPv6 without requiring an external GeoIP file");
                check(runtime["log"]!["loglevel"]!.ToString()=="none" && !runtime.ToJsonString().Contains("\"api\""),"full profiles do not enable logs or administrative listeners");
                check((await plan.ValidateAsync(default)).Success,"bundled Xray validates the complete automatic profile");
                using var api=new CoreDiagnostics(); var native=SingBoxConfig.Build(document,api.Port,api.Secret,false,includeTun:false,xrayEndpoints:plan.Endpoints);
                plan.ConfigureDnsFromRuntime(native);
                check(JsonNode.DeepEquals(JsonNode.Parse(plan.ProcessPlans[0].Configuration)!["dns"],source["dns"]),"configuring the main bootstrap never overwrites the full profile's own DNS");
            }
            foreach(var mutation in new Action<JsonNode>[] {
                root=>root["api"]=new JsonObject { ["tag"]="unsafe" },
                root=>root["routing"]!["script"]="unsafe",
                root=>root["outbounds"]![0]!["streamSettings"]!["tlsSettings"]=new JsonObject { ["certificateFile"]="C:/private.pem" },
                root=>root["routing"]!["rules"]![0]!["ip"]=new JsonArray("ext:private.dat:entry")
            })
            {
                var changed=source.DeepClone(); mutation(changed);
                try { ProfileImporter.Parse(changed.ToJsonString()); throw new Exception("Unsafe full profile accepted"); }
                catch(InvalidDataException) { check(true,"unsupported code, local file dependencies and administrative settings fail before startup"); }
            }
            var mixedAuto = source.DeepClone();
            mixedAuto["remarks"] = "VLESS and Hysteria2 Auto";
            var hysteria = XrayProfile.Parse("hysteria2://fixture@127.0.0.1:10?sni=fixture.example").Outbound;
            hysteria["tag"] = "t2";
            mixedAuto["outbounds"]![1] = hysteria;
            var mixedSubscription = SubscriptionImporter.ParseContent(new JsonArray(mixedAuto).ToJsonString(), allowPartial: true);
            var mixedAutoDocument = ClashConfigDocument.Parse(Path.Combine(home, "mixed-auto.yaml"), mixedSubscription.Yaml);
            using (var mixedAutoPlan = XrayBridgePlan.Create(mixedAutoDocument))
            {
                check(mixedSubscription.CompatibilityWarnings.Count == 0 && mixedAutoDocument.ConnectionCount == 1,
                    "an automatic subscription profile can combine VLESS and Hysteria2");
                var runtime = JsonNode.Parse(mixedAutoPlan.ProcessPlans.Single().Configuration)!;
                check(runtime["outbounds"]![1]!["protocol"]!.ToString() == "hysteria"
                    && JsonNode.DeepEquals(runtime["routing"]!["balancers"], mixedAuto["routing"]!["balancers"])
                    && JsonNode.DeepEquals(runtime["burstObservatory"], mixedAuto["burstObservatory"]),
                    "mixed automatic profiles retain Hysteria2, balancing and observations");
                check((await mixedAutoPlan.ValidateAsync(default)).Success, "bundled Xray validates mixed automatic profiles");
            }
            var simple=(JsonObject)source.DeepClone(); simple["remarks"]="Ordinary JSON"; simple.Remove("burstObservatory"); simple["routing"]!.AsObject().Remove("balancers");
            simple["outbounds"]!.AsArray().RemoveAt(1);
            simple["outbounds"]![0]!["mux"]=new JsonObject { ["enabled"]=true,["concurrency"]=-1,["xudpConcurrency"]=16,["xudpProxyUDP443"]="reject" };
            var array=new JsonArray(source.DeepClone(),JsonNode.Parse(Fixture("Second Auto")),simple.DeepClone());
            var mixed=SubscriptionImporter.ParseContent(array.ToJsonString());
            var legacy=ProfileImporter.Parse("vless://12345678-1234-4234-8234-123456789abc@127.0.0.1:9?security=none#Original");
            check(SubscriptionFormats.Merge(legacy,mixed).Count==4,"an original server variant is retained when JSON adds different mux settings");
            var equivalent=simple.DeepClone(); equivalent["outbounds"]![0]!.AsObject().Remove("mux");
            var equivalentView=SubscriptionImporter.ParseContent(new JsonArray(source.DeepClone(),equivalent).ToJsonString());
            check(SubscriptionFormats.Merge(legacy,equivalentView).Count==2,"equivalent connections across formats are deduplicated without losing automatic profiles");
            var mixedDocument=ClashConfigDocument.Parse(Path.Combine(home,"mixed.yaml"),mixed.Yaml);
            // The transport must leave through the physical gateway even when
            // a virtual VPN owns Windows' default route. Bootstrap configuration
            // must preserve the full profile's own socket options and DNS.
            var remoteSource=source.DeepClone();
            remoteSource["outbounds"]![0]!["settings"]!["vnext"]![0]!["address"]="192.0.2.1";
            var remoteSimple=simple.DeepClone();
            remoteSimple["outbounds"]![0]!["settings"]!["vnext"]![0]!["address"]="192.0.2.2";
            var bindingImport=SubscriptionImporter.ParseContent(new JsonArray(remoteSource,remoteSimple).ToJsonString());
            var bindingDocument=ClashConfigDocument.Parse(Path.Combine(home,"bindings.yaml"),bindingImport.Yaml);
            using(var bindingPlan=XrayBridgePlan.Create(bindingDocument))
            {
                using var api=new CoreDiagnostics();
                var runtime=JsonNode.Parse(SingBoxConfig.Build(bindingDocument,api.Port,api.Secret,false,includeTun:true,xrayEndpoints:bindingPlan.Endpoints))!;
                if(PhysicalUplink.Find() is { } physical)
                    check(runtime["route"]!["default_interface"]!.ToString()==physical&&!runtime["route"]!["auto_detect_interface"]!.GetValue<bool>(),"bridged traffic pins its physical gateway instead of following a virtual default route");
                var sockets=runtime["outbounds"]!.AsArray().Where(item=>item?["type"]?.ToString()=="socks").ToArray();
                check(sockets.Length==2&&sockets.All(item=>item!["server"]!.ToString()=="127.0.0.1"&&item["bind_interface"] is not null),"local Xray bridge sockets keep their loopback interface rather than the external gateway");
                var routeRules=runtime["route"]!["rules"]!.AsArray().ToArray();
                var bypassIndex=Array.FindIndex(routeRules,item=>item?["process_path"]?[0]?.ToString()==XrayBridgePlan.EnginePath);
                var sniffIndex=Array.FindIndex(routeRules,item=>item?["action"]?.ToString()=="sniff");
                check(bypassIndex>=0&&bypassIndex<sniffIndex&&routeRules[bypassIndex]!["outbound"]!.ToString()=="DIRECT","TUN bypasses the owned Xray process before user routing rules to prevent recursion");
                runtime["route"]!["default_interface"]="Fixture physical uplink";
                bindingPlan.ConfigureDnsFromRuntime(runtime.ToJsonString());
                var ordinary=JsonNode.Parse(bindingPlan.ProcessPlans[0].Configuration)!;
                var full=JsonNode.Parse(bindingPlan.ProcessPlans[1].Configuration)!;
                check(ordinary["outbounds"]![0]!["streamSettings"]!["sockopt"]!["interface"]!.ToString()=="Fixture physical uplink"
                    &&full["outbounds"]![0]!["streamSettings"]!["sockopt"]!["interface"]!.ToString()=="Fixture physical uplink","both ordinary and full-profile remote transports are pinned to the selected physical gateway");
                check(full["outbounds"]![1]!["streamSettings"]?["sockopt"]?["interface"] is null,"loopback peers are never pinned to an external network adapter");
                check(JsonNode.DeepEquals(full["dns"],source["dns"])&&ordinary["outbounds"]![0]!["mux"]!["xudpConcurrency"]!.GetValue<int>()==16
                    &&ordinary["outbounds"]![0]!["streamSettings"]!["sockopt"]!["domainStrategy"]!.ToString()=="ForceIP","gateway protection preserves provider DNS and XUDP options while retaining bootstrap resolution");
                bindingPlan.ConfigureDnsFromRuntime(runtime.ToJsonString());
                check(bindingPlan.ProcessPlans[0].Configuration==ordinary.ToJsonString()&&bindingPlan.ProcessPlans[1].Configuration==full.ToJsonString(),"repeated bootstrap configuration preserves existing socket settings");
            }
            using(var mixedPlan=XrayBridgePlan.Create(mixedDocument))
            {
                check(mixedDocument.ConnectionCount==3 && mixedPlan.ProcessPlans.Count==3,"two independent full profiles and ordinary JSON connections coexist without DNS or tag collisions");
                check(JsonNode.Parse(mixedPlan.Configuration)!["outbounds"]![0]!["mux"]!["xudpConcurrency"]!.GetValue<int>()==16,"ordinary JSON connections retain XUDP and mux options");
                using var api=new CoreDiagnostics();
                var runtime=JsonNode.Parse(SingBoxConfig.Build(mixedDocument,api.Port,api.Secret,false,includeTun:false,xrayEndpoints:mixedPlan.Endpoints))!;
                // This lifecycle fixture needs only its random controller and
                // authenticated bridge ports, never the user's fixed proxy port.
                var inbounds=runtime["inbounds"]!.AsArray();
                foreach(var inbound in inbounds.Where(item=>item?["type"]?.ToString()=="mixed").ToArray()) inbounds.Remove(inbound);
                var file=Path.Combine(home,"runtime.json"); File.WriteAllText(file,runtime.ToJsonString());
                var engine=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../Packaging/Engine/sing-box.exe"));
                await core.StartWithBridgeAsync(CoreProcessManager.CreateStartInfo(engine,file),mixedPlan,default);
                check(core.IsRunning && core.BridgeProcessIds.Count==3,"all full-profile and ordinary bridges start under the ownership of the main core");
                var childProcesses=core.BridgeProcessIds.Select(Process.GetProcessById).ToArray();
                try
                {
                    childProcesses[^1].Kill();
                    for(var attempt=0;attempt<150 && core.HasTrackedProcess;attempt++) await Task.Delay(40);
                    check(!core.HasTrackedProcess && childProcesses.All(process=>process.HasExited),"failure of any full-profile child stops the main core and every sibling without leaving an orphan");
                    check(mixedPlan.ProcessPlans.All(item=>item.ConfigPath is null),"private configuration files are removed after every owned child stops");
                }
                finally { foreach(var process in childProcesses) process.Dispose(); }
            }
            var requests=0; var id="aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
            const string link="vless://12345678-1234-4234-8234-123456789abc@100:443?security=none#%E2%AC%87%20WiFi%20%E2%AC%87";
            using var client=new HttpClient(new FakeHandler((request,token)=> {
                requests++;
                check(request.Headers.GetValues("x-hwid").Single()==id,"format negotiation reuses the same own device ID");
                if(requests==1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(Convert.ToBase64String(Encoding.UTF8.GetBytes(link))) });
                check(request.Headers.UserAgent.ToString().Contains("MorphocyteOS/") && request.Headers.UserAgent.ToString().Contains("Happ/compatible")
                    && request.Headers.GetValues("x-device-os").Single()=="Windows","desktop negotiation declares compatibility and the real MorphocyteOS product, not a copied Happ identity");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(array.ToJsonString()) });
            }));
            var refreshed=await SubscriptionImporter.ImportAsync("https://subscription.example/account",default,client,hardwareId:id);
            check(requests==2 && refreshed.SubscriptionUrl=="https://subscription.example/account"
                && ClashConfigDocument.Parse("negotiated.yaml",refreshed.Yaml).ConnectionCount==3,"the user's original URL loads complete automatic profiles without saving an alternate URL");

            var plainList = string.Join('\n', Enumerable.Range(1, 28).Select(index =>
                $"vless://12345678-1234-4234-8234-123456789abc@standard{index}.example:443?security=none#Standard{index}"));
            var desktopList = new JsonArray(Enumerable.Range(1, 35).Select(index =>
            {
                var item = equivalent.DeepClone();
                item["remarks"] = $"Desktop{index}";
                return item;
            }).ToArray());
            var requestedFormats = new List<bool>();
            using var split = new HttpClient(new FakeHandler((request, _) =>
            {
                var desktop = request.Headers.UserAgent.ToString().Contains("Happ/compatible");
                requestedFormats.Add(desktop);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(Convert.ToBase64String(Encoding.UTF8.GetBytes(desktop ? desktopList.ToJsonString() : plainList)))
                });
            }));
            var combined = await SubscriptionImporter.ImportAsync("https://subscription.example/account", default, split, hardwareId: id);
            var combinedDocument = ClashConfigDocument.Parse("combined.yaml", combined.Yaml);
            check(requestedFormats.SequenceEqual(new[] { false, true }) && combinedDocument.ConnectionCount == 63,
                "separate Base64 link and JSON lists of 28 and 35 nodes are combined even without section headings");
            check(combinedDocument.ProxyNodes.Count(node => node.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("type")].ToString() == "vless") == 28,
                "link-only servers keep their native transport instead of moving to the Xray bridge");
            var combinedPath = Path.Combine(home, "combined.yaml");
            File.WriteAllText(combinedPath, combined.Yaml);
            var singBoxPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Packaging/Engine/sing-box.exe"));
            check((await CoreProcessManager.ValidateAsync(singBoxPath, combinedPath, default)).Success,
                "both engines validate a merged subscription containing native public VLESS links and Xray JSON servers");
            using var unavailable = new HttpClient(new FakeHandler((request, _) => Task.FromResult(
                request.Headers.UserAgent.ToString().Contains("Happ/compatible")
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(plainList) })));
            var basic = await SubscriptionImporter.ImportAsync("https://subscription.example/account", default, unavailable, hardwareId: id);
            check(ClashConfigDocument.Parse("basic.yaml", basic.Yaml).ConnectionCount == 28 && basic.CompatibilityWarnings.Count == 1,
                "a failed supplemental request requires confirmation before applying the basic list");
        }
        finally { await core.StopAsync(); Directory.Delete(home,true); }
    }
}
