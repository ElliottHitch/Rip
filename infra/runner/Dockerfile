FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:299c82e09d90ddef2e7cd660e12ea3c1863a7227e8cf26e686b6021912ac4f3f

ARG RUNNER_VERSION=2.337.0
ARG RUNNER_SHA256=9b1dc70626422526e3c94767cf024896beb15da5342a3f4819bf2feac13e0393
RUN apt-get update && apt-get install -y --no-install-recommends \
      ca-certificates curl git gh jq unzip libicu74 libssl3t64 libkrb5-3 zlib1g \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 1001 runner \
    && mkdir -p /opt/runner /runner \
    && curl -fsSL "https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/actions-runner-linux-arm64-${RUNNER_VERSION}.tar.gz" -o /tmp/runner.tar.gz \
    && echo "${RUNNER_SHA256}  /tmp/runner.tar.gz" | sha256sum --check \
    && tar -xzf /tmp/runner.tar.gz -C /opt/runner \
    && rm /tmp/runner.tar.gz \
    && chown -R runner:runner /runner /opt/runner
COPY --chmod=755 entrypoint.sh trusted-job.sh /usr/local/bin/
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    ACTIONS_RUNNER_HOOK_JOB_STARTED=/usr/local/bin/trusted-job.sh
USER runner
WORKDIR /runner
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
