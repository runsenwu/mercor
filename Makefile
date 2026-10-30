# Wraps docker compose. Recipes are plain POSIX sh.
COMPOSE ?= docker compose -f deploy/docker-compose.yml $(if $(wildcard .env),--env-file .env)
K6_OUTPUT ?= experimental-prometheus-rw
SCENARIO ?= smoke
K6_ARGS ?=
SERVICE ?=
N ?= 2
DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo $(HOME)/.dotnet/dotnet)
export UID := $(shell id -u)
export GID := $(shell id -g)

.DEFAULT_GOAL := help
.PHONY: help up down clean ps logs scale-api scale-worker seed flush-cache load smoke ramp cache-miss spike ratelimit test urls config

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*## "}; {printf "  %-14s %s\n", $$1, $$2}'

up: ## Build images, start the stack, wait until healthy, print URLs
	$(COMPOSE) up -d --build --wait --wait-timeout 300
	@$(MAKE) --no-print-directory urls

down: ## Stop the stack (keeps volumes)
	$(COMPOSE) --profile loadtest down --remove-orphans

clean: ## Stop the stack and delete all volumes
	$(COMPOSE) --profile loadtest down -v --remove-orphans

ps: ## Show containers
	$(COMPOSE) ps

logs: ## Follow logs (SERVICE=api for one service)
	$(COMPOSE) logs -f --tail=100 $(SERVICE)

scale-api: ## Scale the api: make scale-api N=4 (nginx picks it up within 5 s)
	$(COMPOSE) up -d --no-recreate --scale api=$(N) api

scale-worker: ## Scale the worker: make scale-worker N=3
	$(COMPOSE) up -d --no-recreate --scale worker=$(N) worker

seed: ## Insert 100k load-test links (loadtest/seed.sql)
	$(COMPOSE) exec -T postgres psql -v ON_ERROR_STOP=1 -U shortener -d shortener < loadtest/seed.sql

flush-cache: ## Empty Redis (cold cache)
	$(COMPOSE) exec -T redis redis-cli FLUSHALL

load: ## Run a k6 scenario: make load SCENARIO=ramp [K6_ARGS="-e FOO=bar"] (exit 99 = thresholds crossed past the knee, expected)
	testid="$(SCENARIO)-$$(date +%Y%m%d-%H%M%S)"; echo "testid=$$testid"; \
	$(COMPOSE) --profile loadtest run --rm k6 run -o $(K6_OUTPUT) --tag testid=$$testid $(K6_ARGS) /scripts/$(SCENARIO).js || [ $$? -eq 99 ]

smoke: ## k6: 1-minute check of every endpoint
	@$(MAKE) --no-print-directory load SCENARIO=$@

ramp: ## k6: 3-minute step ramp 500 -> 5000 rps, finds the saturation point
	@$(MAKE) --no-print-directory load SCENARIO=$@

cache-miss: ## k6: same ramp with codes spread evenly (mostly cache misses)
	@$(MAKE) --no-print-directory load SCENARIO=$@

spike: ## k6: steady baseline, sudden 10x spike, recovery
	@$(MAKE) --no-print-directory load SCENARIO=$@

ratelimit: ## k6: anonymous client hits its limit while a keyed client is unaffected
	@$(MAKE) --no-print-directory load SCENARIO=$@

test: ## Run unit tests
	$(DOTNET) test --solution Shortener.slnx

config: ## Validate the compose file
	$(COMPOSE) --profile loadtest config -q && echo "compose config OK"

urls: ## Print the URLs
	@echo "API (via nginx):  http://localhost:8080"
	@echo "API docs:         http://localhost:8080/docs"
	@echo "OpenAPI:          http://localhost:8080/openapi/v1.json"
	@echo "Grafana:          http://localhost:3000   (anonymous; admin/admin)"
	@echo "Prometheus:       http://localhost:9090"
	@echo "RabbitMQ UI:      http://localhost:15672  (shortener/shortener)"
