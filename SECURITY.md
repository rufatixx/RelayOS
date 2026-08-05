# Security Policy

RelayOS is an experimental MVP and has not received an independent security
review. Please do not use it for emergencies, medical care, public safety,
military use, or any situation where delayed, duplicated, disclosed, or lost
data could cause harm.

## Reporting a vulnerability

Please avoid public issues for sensitive security reports.

If you believe you found a vulnerability:

1. Create a private report through GitHub Security Advisories if available.
2. If private advisories are not available, contact the maintainer through the
   official GitHub profile and request a private reporting channel.
3. Include a concise description, affected commit or version, reproduction
   steps, expected impact, and any suggested fix.

Do not include private keys, real user data, or exploit material beyond what is
needed to demonstrate the issue.

## Scope

Security-sensitive areas include:

- packet encryption and authentication;
- key import, export, derivation, and validation;
- queue persistence and replay behavior;
- packet parsing and hostile input handling;
- transport contracts and future physical adapter behavior;
- documentation that could cause users to mistake the simulator for production
  networking.

## Expectations

RelayOS maintainers will try to acknowledge serious reports promptly, but this
is an early personal project and response times may vary. Public credit is
welcome when a fix ships, unless the reporter asks to remain private.
