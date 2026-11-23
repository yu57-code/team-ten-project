
using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

public interface IReviewApiService
{
    Task<IReadOnlyList<ApiReviewDto>> GetPendingAsync(
        CancellationToken cancellationToken = default);

    Task<ApiReviewDto?> GetByIdAsync(
        long reportId,
        CancellationToken cancellationToken = default);
}
