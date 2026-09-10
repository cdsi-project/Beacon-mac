using CDSI.Agent.Core.Identity;

namespace CDSI.Agent.Core.Abstractions;

public interface IClientIdentityProvider
{
    ClientIdentity GetOrCreate();
}
