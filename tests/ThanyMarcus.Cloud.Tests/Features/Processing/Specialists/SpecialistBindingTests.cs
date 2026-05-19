using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Specialists;

[Collection(PostgresCollection.Name)]
public sealed class SpecialistBindingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Vlm_only_claims_ollama_sidecar_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await SpecialistWorkerBaseTests.SeedQueuedTaskAsync(postgres, AttachmentKind.File, ExtractionTaskSidecar.Docling);
        await SpecialistWorkerBaseTests.SeedQueuedTaskAsync(postgres, AttachmentKind.Image, ExtractionTaskSidecar.Ollama);

        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString);
        var vlm = SpecialistTestHost.NewVlm(sp);
        var claimed = await vlm.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        claimed!.TargetSidecar.ShouldBe(ExtractionTaskSidecar.Ollama);
    }

    [Fact]
    public async Task Docling_only_claims_docling_sidecar_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await SpecialistWorkerBaseTests.SeedQueuedTaskAsync(postgres, AttachmentKind.File, ExtractionTaskSidecar.Docling);

        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString);
        var worker = SpecialistTestHost.NewDocling(sp);
        var claimed = await worker.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        claimed!.TargetSidecar.ShouldBe(ExtractionTaskSidecar.Docling);
    }

    [Fact]
    public async Task Parakeet_only_claims_parakeet_sidecar_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await SpecialistWorkerBaseTests.SeedQueuedTaskAsync(postgres, AttachmentKind.Voice, ExtractionTaskSidecar.Parakeet);

        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString);
        var worker = SpecialistTestHost.NewParakeet(sp);
        var claimed = await worker.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        claimed!.TargetSidecar.ShouldBe(ExtractionTaskSidecar.Parakeet);
    }

    [Fact]
    public async Task Url_fetcher_only_claims_url_sidecar_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await SpecialistWorkerBaseTests.SeedQueuedTaskAsync(postgres, AttachmentKind.Url, ExtractionTaskSidecar.Url);

        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString);
        var worker = SpecialistTestHost.NewUrl(sp);
        var claimed = await worker.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        claimed!.TargetSidecar.ShouldBe(ExtractionTaskSidecar.Url);
    }

    [Fact]
    public async Task Video_splitter_only_claims_video_sidecar_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await SpecialistWorkerBaseTests.SeedQueuedTaskAsync(postgres, AttachmentKind.File, ExtractionTaskSidecar.Video);

        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString);
        var worker = SpecialistTestHost.NewVideo(sp);
        var claimed = await worker.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        claimed!.TargetSidecar.ShouldBe(ExtractionTaskSidecar.Video);
    }
}
