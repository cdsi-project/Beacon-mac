DOTNET_BIN ?= dotnet
CONFIGURATION ?= Release
BEACON_ARCH ?= arm64

.PHONY: restore build test app run clean

restore:
	$(DOTNET_BIN) restore CDSI.Agent.Mac.slnx

build:
	$(DOTNET_BIN) build CDSI.Agent.Mac.slnx -c $(CONFIGURATION) --no-restore

test:
	$(DOTNET_BIN) test CDSI.Agent.Mac.slnx -c $(CONFIGURATION) --no-restore

app:
	DOTNET_BIN="$(DOTNET_BIN)" CONFIGURATION="$(CONFIGURATION)" BEACON_ARCH="$(BEACON_ARCH)" ./scripts/build-app.sh

run:
	$(DOTNET_BIN) run --project CDSI.Agent.Mac/CDSI.Agent.Mac.csproj

clean:
	$(DOTNET_BIN) clean CDSI.Agent.Mac.slnx
	rm -rf build
