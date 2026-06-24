namespace TravelwAI.Business.Interfaces;

public interface ITravelService
{
    Task<Dictionary<string, object?>?> GetProvinceByNameAsync(string provinceName);
    Task<Dictionary<string, object?>?> GetProvinceWithDestinationsAsync(string provinceId);
}
