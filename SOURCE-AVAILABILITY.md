# XA Slave source availability

XA Slave remains licensed under **AGPL-3.0-or-later**, as declared in
`XASlave/XASlave.csproj`. The complete license is included as `LICENSE`.
Obfuscating release binaries does not restrict rights granted by that license
or remove any corresponding-source obligation. No ownership transfer or
closed-source relicensing has been established by this audit.

Before distributing a binary, the release operator must provide recipients
equivalent access to its complete corresponding source under the applicable
AGPL terms. Source must be the preferred form for modification and include
changes used in that exact artifact, interface definitions, required catalogs,
project/lock files, and scripts/configuration needed to generate, protect and
install it. An obfuscated decompilation or renaming map is not a substitute.
Preserve applicable upstream notices and license terms.

The inspected local history contains 43 commits attributed to one author
identity. This is attribution evidence, not proof of exclusive ownership,
assignments, absence of imported code, or contributor consent to relicensing.
The worktree contains substantial uncommitted and untracked work. Its existing
ignore rules omit Python release helpers and signature sources. Consequently,
the current public Git snapshot or commit ID alone is **not verified complete
corresponding source for a newly built artifact**.

The advanced receipt-based publication interface requires an operator-reviewed source receipt bound to
the same source identity and source inventory used for compilation, with an
actual recipient-accessible distribution location and source archive digest.
The operator must include required ignored inputs deliberately, scan the
separate source distribution for credentials/private user data, retain full
license/attribution notices, and verify that recipients can obtain it. If
required source is withheld or availability cannot be verified, publication
must fail. Source belongs in a separately reviewed distribution; it is rejected
from the binary updater ZIP. No source distribution or upload occurred in the
2026-09-07 source/static handoff. The normal numbered Git release workflow
publishes a filtered source tree that excludes signature files and private
tooling; it does not upload the private corresponding-source archive or establish
that the filtered tree is complete corresponding source. The operator remains
responsible for the source-availability obligations described above.

Network deployment of a modified AGPL-covered service can add section 13
source-offer obligations. The optional RVA-service proposal in
`docs/protection-server-design.md` requires a separate ownership/license review;
moving required source or signatures to a server does not automatically make
withholding them lawful.

The release operator must resolve any ownership or contribution dispute before
distribution. Preserve recipients' existing rights while obtaining any needed
permissions; do not silently change the license declaration or remove notices.
