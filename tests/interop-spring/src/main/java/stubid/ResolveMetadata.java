package stubid;

import org.springframework.security.oauth2.client.registration.ClientRegistration;
import org.springframework.security.oauth2.client.registration.ClientRegistrations;

/**
 * Resolves StubID's metadata the way a Spring application does.
 *
 * <p>Spring is the strictest of the three client stacks about two things. It derives candidate
 * metadata locations from the issuer and tries them in order, and a path-bearing issuer like
 * StubID's produces more than one candidate. It then asserts that the issuer inside the
 * document equals the one it was configured with, and fails hard if it does not.
 *
 * <p>Neither check is exercised by the .NET handler, which is why this exists.
 */
public final class ResolveMetadata {

    public static void main(String[] args) {
        String issuer = System.getenv().getOrDefault("STUBID_AUTHORITY", "http://localhost:18080/op");

        ClientRegistration.Builder builder = ClientRegistrations.fromIssuerLocation(issuer);
        ClientRegistration registration = builder
                .registrationId("stubid")
                .clientId("0a775a87-878c-4b83-abe3-ee29c720c3e7")
                .clientSecret("the-secret-the-existing-configuration-carries")
                .build();

        ClientRegistration.ProviderDetails provider = registration.getProviderDetails();

        expect(issuer.equals(provider.getIssuerUri()),
                "the issuer came back as " + provider.getIssuerUri() + ", expected " + issuer);
        expect(provider.getAuthorizationUri().endsWith("/op/connect/authorize"),
                "unexpected authorization endpoint: " + provider.getAuthorizationUri());
        expect(provider.getTokenUri().endsWith("/op/connect/token"),
                "unexpected token endpoint: " + provider.getTokenUri());
        expect(provider.getJwkSetUri().endsWith("/op/.well-known/openid-configuration/jwks"),
                "unexpected key set location: " + provider.getJwkSetUri());

        System.out.println("  metadata resolved from a path-bearing issuer");
        System.out.println("  the issuer matched the configured authority: " + provider.getIssuerUri());
        System.out.println("  the key set was found at its non-standard location");
        System.out.println("Spring Security accepted StubID");
    }

    private static void expect(boolean condition, String message) {
        if (!condition) {
            System.err.println(message);
            System.exit(1);
        }
    }
}
