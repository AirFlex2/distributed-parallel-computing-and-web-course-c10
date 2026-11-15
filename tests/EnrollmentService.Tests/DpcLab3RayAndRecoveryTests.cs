using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using EnrollmentService.Services.Processing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class E2EHttpEndpointTests
{
    [SkippableFact]
    public async Task Dpc_O31_RayTaskClient_ExecutesThroughReceiver_UpdatesDoneRosterAndAppliedEffects()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = $"stud-dpc-o31-{Guid.NewGuid():N}"[..24];
        string groupId = $"GRP-DPC-O31-{Guid.NewGuid():N}"[..24];
        string discId = $"DISC-DPC-O31-{Guid.NewGuid():N}"[..24];

        using var customFactory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RAY_DISPATCHER_URL", "");
            b.UseSetting("IN_FLIGHT_LIMIT", "3");
            b.UseSetting("SLOW_CALL_MIN_MS", "20");
            b.UseSetting("SLOW_CALL_MAX_MS", "30");
        });

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 5,
                available: 5,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1),
                ects: 3.0m,
                dpcSandboxEnabled: true);

            using var dpcClient = customFactory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            using var postResp = await dpcClient.PostAsync(
                "/v1/enrollment-requests",
                new StringContent(
                    JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                    Encoding.UTF8,
                    "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, postResp.StatusCode);

            using var postDoc = JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());
            Guid requestId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            await WaitForTerminalStatusAsync(dpcClient, requestId);

            using var statusResp = await dpcClient.GetAsync($"/v1/enrollment-requests/{requestId}");
            Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
            using var statusDoc = JsonDocument.Parse(await statusResp.Content.ReadAsStringAsync());
            Assert.Equal("done", statusDoc.RootElement.GetProperty("status").GetString());

            await using var conn = await TestEnvironment.OpenConnectionAsync();

            await using (var cmdEff = new NpgsqlCommand(
                "SELECT status, roster_recorded, delivery_count, worker_id FROM dpc_applied_effects WHERE request_id = @rid;", conn))
            {
                cmdEff.Parameters.AddWithValue("rid", requestId);
                await using var rdr = await cmdEff.ExecuteReaderAsync();
                Assert.True(await rdr.ReadAsync());
                Assert.Equal("done", rdr.GetString(0));
                Assert.True(rdr.GetBoolean(1));
                Assert.Equal(1, rdr.GetInt32(2));
                Assert.StartsWith("inproc:", rdr.GetString(3), StringComparison.Ordinal);
            }

            await using (var cmdRoster = new NpgsqlCommand(
                "SELECT COUNT(*)::INT FROM decanate_roster WHERE request_id = @rid;", conn))
            {
                cmdRoster.Parameters.AddWithValue("rid", requestId);
                Assert.Equal(1, (int)(await cmdRoster.ExecuteScalarAsync())!);
            }

            await using (var cmdGroup = new NpgsqlCommand(
                "SELECT available FROM discipline_group WHERE id = @gid;", conn))
            {
                cmdGroup.Parameters.AddWithValue("gid", groupId);
                Assert.Equal(4, (int)(await cmdGroup.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Dpc_O32_InFlightLimit_CapsConcurrentExecutions_AndExcessWaitsInBoundedQueue()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        const int inFlightLimit = 2;
        const int totalRequests = 6;
        string groupId = $"GRP-DPC-O32-{Guid.NewGuid():N}"[..24];
        string discId = $"DISC-DPC-O32-{Guid.NewGuid():N}"[..24];
        string studentPrefix = $"stud-dpc-o32-{Guid.NewGuid():N}"[..18];
        var studentIds = Enumerable.Range(1, totalRequests).Select(i => $"{studentPrefix}-{i}").ToArray();

        using var customFactory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("WORKERS", "6");
            b.UseSetting("IN_FLIGHT_LIMIT", inFlightLimit.ToString());
            b.UseSetting("QUEUE_CAPACITY", "50");
            b.UseSetting("ALLOW_TEST_OVERRIDES", "true");
            b.UseSetting("RAY_DISPATCHER_URL", "");
            b.UseSetting("SLOW_CALL_MIN_MS", "180");
            b.UseSetting("SLOW_CALL_MAX_MS", "220");
        });

        await TestEnvironment.CleanupTestDataAsync(null, groupId, discId);
        try
        {
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 10,
                available: 10,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1),
                ects: 3.0m,
                dpcSandboxEnabled: true);

            foreach (var sid in studentIds)
            {
                await EnsureTestStudentAsync(sid, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            }

            using var dpcClient = customFactory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            // Скидаємо метрики max_in_flight_observed і встановлюємо ліміт незавершених задач = 2
            using var resetResp = await dpcClient.PostAsync(
                $"/v1/admin/reset?unsafeMode=false&inFlightLimit={inFlightLimit}&slowCallMinMs=180&slowCallMaxMs=220",
                null);
            Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);

            var sendTasks = studentIds.Select(sid => dpcClient.PostAsync(
                "/v1/enrollment-requests",
                new StringContent(
                    JsonSerializer.Serialize(new { student_id = sid, group_id = groupId }),
                    Encoding.UTF8,
                    "application/json"))).ToArray();

            var postResponses = await Task.WhenAll(sendTasks);
            var requestIds = new List<Guid>(totalRequests);
            foreach (var resp in postResponses)
            {
                Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                requestIds.Add(Guid.Parse(doc.RootElement.GetProperty("id").GetString()!));
            }

            await Task.WhenAll(requestIds.Select(rid => WaitForTerminalStatusAsync(dpcClient, rid)));

            using var metricsResp = await dpcClient.GetAsync("/metrics");
            Assert.Equal(HttpStatusCode.OK, metricsResp.StatusCode);
            using var metricsDoc = JsonDocument.Parse(await metricsResp.Content.ReadAsStringAsync());
            var root = metricsDoc.RootElement;

            Assert.Equal(inFlightLimit, root.GetProperty("configuration").GetProperty("in_flight_limit").GetInt32());
            long maxObserved = root.GetProperty("max_in_flight_observed").GetInt64();
            Assert.InRange(maxObserved, 1, inFlightLimit);
            Assert.Equal(0, root.GetProperty("in_flight").GetInt64());

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using (var cmdRoster = new NpgsqlCommand("SELECT COUNT(*)::INT FROM decanate_roster WHERE group_id = @gid;", conn))
            {
                cmdRoster.Parameters.AddWithValue("gid", groupId);
                Assert.Equal(totalRequests, (int)(await cmdRoster.ExecuteScalarAsync())!);
            }
            await using (var cmdAvail = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @gid;", conn))
            {
                cmdAvail.Parameters.AddWithValue("gid", groupId);
                Assert.Equal(10 - totalRequests, (int)(await cmdAvail.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(null, groupId, discId);
            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var delStudents = new NpgsqlCommand("DELETE FROM student WHERE id LIKE @prefix;", conn);
            delStudents.Parameters.AddWithValue("prefix", studentPrefix + "%");
            await delStudents.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Dpc_O33_RayEffectReceiver_DuplicateDelivery_IsIdempotent_AndMutatesDomainOnce()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = $"stud-dpc-o33-{Guid.NewGuid():N}"[..24];
        string groupId = $"GRP-DPC-O33-{Guid.NewGuid():N}"[..24];
        string discId = $"DISC-DPC-O33-{Guid.NewGuid():N}"[..24];
        Guid requestId = Guid.NewGuid();

        using var customFactory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("INTERNAL_RECEIVER_SECRET", "c10-test-ray-secret");
        });

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, requestId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 5,
                available: 5,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1),
                ects: 3.0m,
                dpcSandboxEnabled: true);

            using var scope = customFactory.Services.CreateScope();
            var connFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
            var reqRepo = scope.ServiceProvider.GetRequiredService<EnrollmentRequestRepository>();
            var receiver = scope.ServiceProvider.GetRequiredService<RayEffectReceiverService>();

            var queuedItem = new QueuedRequestItem
            {
                Id = requestId,
                StudentId = studentId,
                GroupId = groupId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                EnqueueTimestamp = System.Diagnostics.Stopwatch.GetTimestamp()
            };

            await using (var setupConn = await connFactory.OpenConnectionAsync())
            {
                bool inserted = await reqRepo.TryInsertQueuedRequestAsync(queuedItem, setupConn);
                Assert.True(inserted);
            }

            // 1. Перша доставка результату
            var firstResp = await receiver.ApplyEffectAsync(new RayEffectApplyRequest(
                RequestId: requestId,
                StudentId: studentId,
                GroupId: groupId,
                IsUnsafe: false,
                WorkerId: "ray:node-1",
                RayAttempt: 1));
            Assert.Equal("done", firstResp.Status);
            Assert.False(firstResp.AlreadyApplied);
            Assert.Equal(1, firstResp.DeliveryCount);
            Assert.True(firstResp.RosterRecorded);

            // 2. Друга доставка (повтор після втрати ACK)
            var secondResp = await receiver.ApplyEffectAsync(new RayEffectApplyRequest(
                RequestId: requestId,
                StudentId: studentId,
                GroupId: groupId,
                IsUnsafe: false,
                WorkerId: "ray:node-2",
                RayAttempt: 2));
            Assert.Equal("done", secondResp.Status);
            Assert.True(secondResp.AlreadyApplied);
            Assert.Equal(2, secondResp.DeliveryCount);
            Assert.True(secondResp.RosterRecorded);

            // 3. Перевірка захисту X-Ray-Internal-Secret та третього HTTP-виклику /v1/internal/ray-effect-receiver
            using var httpClient = customFactory.CreateClient();
            var bodyJson = JsonSerializer.Serialize(new RayEffectApplyRequest(
                RequestId: requestId,
                StudentId: studentId,
                GroupId: groupId,
                IsUnsafe: false,
                WorkerId: "ray:node-3",
                RayAttempt: 3));

            using (var unauthReq = new HttpRequestMessage(HttpMethod.Post, "/v1/internal/ray-effect-receiver")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            })
            {
                unauthReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);
                Assert.Equal(HttpStatusCode.Forbidden, (await httpClient.SendAsync(unauthReq)).StatusCode);
            }

            using (var authReq = new HttpRequestMessage(HttpMethod.Post, "/v1/internal/ray-effect-receiver")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            })
            {
                authReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);
                authReq.Headers.Add("X-Ray-Internal-Secret", "c10-test-ray-secret");

                using var httpResp = await httpClient.SendAsync(authReq);
                Assert.Equal(HttpStatusCode.OK, httpResp.StatusCode);
                using var httpDoc = JsonDocument.Parse(await httpResp.Content.ReadAsStringAsync());
                Assert.True(httpDoc.RootElement.GetProperty("already_applied").GetBoolean());
                Assert.Equal(3, httpDoc.RootElement.GetProperty("delivery_count").GetInt32());
            }

            // 4. Предметні інваріанти БД застосовані рівно ОДИН раз
            await using var chkConn = await TestEnvironment.OpenConnectionAsync();
            await using (var cmdRoster = new NpgsqlCommand("SELECT COUNT(*)::INT FROM decanate_roster WHERE request_id = @rid;", chkConn))
            {
                cmdRoster.Parameters.AddWithValue("rid", requestId);
                Assert.Equal(1, (int)(await cmdRoster.ExecuteScalarAsync())!);
            }
            await using (var cmdGroup = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @gid;", chkConn))
            {
                cmdGroup.Parameters.AddWithValue("gid", groupId);
                Assert.Equal(4, (int)(await cmdGroup.ExecuteScalarAsync())!);
            }
            await using (var cmdEff = new NpgsqlCommand("SELECT delivery_count FROM dpc_applied_effects WHERE request_id = @rid;", chkConn))
            {
                cmdEff.Parameters.AddWithValue("rid", requestId);
                Assert.Equal(3, (int)(await cmdEff.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, requestId);
        }
    }

    [SkippableFact]
    public async Task Dpc_O34_FaultAfterEffectBeforeAck_CrashesOnce_DeletesFlag_RetriesAndRecoversWithoutDuplicate()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = $"stud-dpc-o34-{Guid.NewGuid():N}"[..24];
        string groupId = $"GRP-DPC-O34-{Guid.NewGuid():N}"[..24];
        string discId = $"DISC-DPC-O34-{Guid.NewGuid():N}"[..24];
        string tempFaultsDir = Path.Combine(Path.GetTempPath(), $"c10_dpc_o34_faults_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFaultsDir);
        string flagFile = Path.Combine(tempFaultsDir, "after_effect_before_ack");

        using var customFactory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("FAULTS", "on");
            b.UseSetting("FAULTS_DIR", tempFaultsDir);
            b.UseSetting("ALLOW_TEST_OVERRIDES", "true");
            b.UseSetting("RAY_DISPATCHER_URL", "");
            b.UseSetting("RAY_TASK_MAX_RETRIES", "3");
            b.UseSetting("SLOW_CALL_MIN_MS", "10");
            b.UseSetting("SLOW_CALL_MAX_MS", "20");
        });

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 5,
                available: 5,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1),
                ects: 3.0m,
                dpcSandboxEnabled: true);

            await File.WriteAllTextAsync(flagFile, "1");
            Assert.True(File.Exists(flagFile));

            using var dpcClient = customFactory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            using var postResp = await dpcClient.PostAsync(
                "/v1/enrollment-requests",
                new StringContent(
                    JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                    Encoding.UTF8,
                    "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, postResp.StatusCode);

            using var postDoc = JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());
            Guid requestId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            await WaitForTerminalStatusAsync(dpcClient, requestId);

            // 1. Прапорець /faults/after_effect_before_ack атомарно захоплено і видалено при першому спрацюванні
            Assert.False(File.Exists(flagFile));

            // 2. Статус заявки — done, а доставка ефекту зафіксована щонайменше двічі (перша спроба впала до ack, друга автоматично повторила)
            using var getResp = await dpcClient.GetAsync($"/v1/enrollment-requests/{requestId}");
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
            Assert.Equal("done", getDoc.RootElement.GetProperty("status").GetString());

            await using var conn = await TestEnvironment.OpenConnectionAsync();

            await using (var cmdEff = new NpgsqlCommand("SELECT status, delivery_count FROM dpc_applied_effects WHERE request_id = @rid;", conn))
            {
                cmdEff.Parameters.AddWithValue("rid", requestId);
                await using var rdr = await cmdEff.ExecuteReaderAsync();
                Assert.True(await rdr.ReadAsync());
                Assert.Equal("done", rdr.GetString(0));
                Assert.True(rdr.GetInt32(1) >= 2, $"Очікувався повтор після збою до ack (delivery_count >= 2), фактично: {rdr.GetInt32(1)}");
            }

            await using (var cmdRoster = new NpgsqlCommand("SELECT COUNT(*)::INT FROM decanate_roster WHERE request_id = @rid;", conn))
            {
                cmdRoster.Parameters.AddWithValue("rid", requestId);
                Assert.Equal(1, (int)(await cmdRoster.ExecuteScalarAsync())!);
            }

            await using (var cmdSeats = new NpgsqlCommand("SELECT seats, available FROM discipline_group WHERE id = @gid;", conn))
            {
                cmdSeats.Parameters.AddWithValue("gid", groupId);
                await using var rdr = await cmdSeats.ExecuteReaderAsync();
                Assert.True(await rdr.ReadAsync());
                int seats = rdr.GetInt32(0);
                int avail = rdr.GetInt32(1);
                Assert.Equal(4, avail);
                Assert.True((seats - avail) <= seats);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
            if (Directory.Exists(tempFaultsDir))
            {
                Directory.Delete(tempFaultsDir, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task Dpc_O33_O34_LostDispatcherResponseAfterRejectedEffect_ReconcilesFromAppliedEffects_AndDoesNotOverwriteWithFailed()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = $"stud-dpc-loss-{Guid.NewGuid():N}"[..24];
        string groupId = $"GRP-DPC-LOSS-{Guid.NewGuid():N}"[..24];
        string discId = $"DISC-DPC-LOSS-{Guid.NewGuid():N}"[..24];
        Guid requestId = Guid.NewGuid();

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, requestId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 5,
                available: 0,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1),
                ects: 3.0m,
                dpcSandboxEnabled: true);

            using var scope = _factory.Services.CreateScope();
            var connFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
            var reqRepo = scope.ServiceProvider.GetRequiredService<EnrollmentRequestRepository>();
            var groupRepo = scope.ServiceProvider.GetRequiredService<GroupRepository>();
            var enrollmentRepo = scope.ServiceProvider.GetRequiredService<EnrollmentRepository>();
            var decanateRepo = scope.ServiceProvider.GetRequiredService<DecanateRepository>();
            var metrics = scope.ServiceProvider.GetRequiredService<MetricsCollector>();
            var options = scope.ServiceProvider.GetRequiredService<ServiceOptions>();
            var webNotifier = scope.ServiceProvider.GetRequiredService<IWebEnrollmentNotifier>();
            var receiver = scope.ServiceProvider.GetRequiredService<RayEffectReceiverService>();
            var queue = scope.ServiceProvider.GetRequiredService<IBoundedRequestQueue>();

            var queuedItem = new QueuedRequestItem
            {
                Id = requestId,
                StudentId = studentId,
                GroupId = groupId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                EnqueueTimestamp = System.Diagnostics.Stopwatch.GetTimestamp()
            };

            await using (var setupConn = await connFactory.OpenConnectionAsync())
            {
                Assert.True(await reqRepo.TryInsertQueuedRequestAsync(queuedItem, setupConn));
            }

            var fakeRayClient = new RejectedThenLostResponseRayTaskClient(receiver);
            var processor = new EnrollmentRequestProcessor(
                reqRepo, groupRepo, enrollmentRepo, decanateRepo, connFactory, metrics, options, webNotifier, receiver, fakeRayClient);

            await processor.ProcessItemAsync(1, queuedItem, isAsyncModel: true, queue, CancellationToken.None);

            await using var chkConn = await TestEnvironment.OpenConnectionAsync();
            var storedReq = await reqRepo.GetStatusByIdAsync(requestId);
            Assert.NotNull(storedReq);
            Assert.Equal("rejected", storedReq!.Status);
            Assert.False(string.IsNullOrWhiteSpace(storedReq.RejectionReason));

            var storedEffect = await reqRepo.GetAppliedEffectDecisionAsync(requestId, chkConn);
            Assert.NotNull(storedEffect);
            Assert.Equal("rejected", storedEffect!.Status);

            // Пряма спроба перезаписати термінальний стан 'rejected' на 'failed' також має блокуватися
            await reqRepo.UpdateStatusAsync(
                requestId,
                "failed",
                "Перезапис не повинен спрацювати",
                null,
                DateTimeOffset.UtcNow,
                chkConn);

            var afterOverwriteAttempt = await reqRepo.GetStatusByIdAsync(requestId);
            Assert.NotNull(afterOverwriteAttempt);
            Assert.Equal("rejected", afterOverwriteAttempt!.Status);
            Assert.Equal(storedReq.RejectionReason, afterOverwriteAttempt.RejectionReason);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, requestId);
        }
    }

    private sealed class RejectedThenLostResponseRayTaskClient : IRayTaskClient
    {
        private readonly RayEffectReceiverService _receiver;

        public RejectedThenLostResponseRayTaskClient(RayEffectReceiverService receiver)
        {
            _receiver = receiver;
        }

        public async Task<(string Status, string? Reason, bool Committed)> ExecuteAsync(
            QueuedRequestItem item,
            bool isUnsafe,
            CancellationToken cancellationToken = default)
        {
            var res = await _receiver.ApplyEffectAsync(new RayEffectApplyRequest(
                RequestId: item.Id,
                StudentId: item.StudentId,
                GroupId: item.GroupId,
                EnrollmentId: item.EnrollmentId,
                RequestedEnrollmentId: item.RequestedEnrollmentId,
                IsUnsafe: isUnsafe,
                WorkerId: "ray:test-lost-response",
                RayAttempt: 1), trackedItem: item, cancellationToken);

            Assert.Equal("rejected", res.Status);
            throw new HttpRequestException("Simulated lost HTTP response from Ray dispatcher after rejected decision.");
        }
    }
}
