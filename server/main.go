package main

import (
	"compress/gzip"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"
)

const defaultMaxBodyBytes int64 = 1 << 20

type server struct {
	store        *store
	token        string
	maxBodyBytes int64
}

func main() {
	addr := getenv("PFREPORT_ADDR", ":8787")
	dataPath := getenv("PFREPORT_DATA", "data/pfreport.db")
	maxBodyBytes := defaultMaxBodyBytes
	if raw := os.Getenv("PFREPORT_MAX_BODY_BYTES"); raw != "" {
		parsed, err := strconv.ParseInt(raw, 10, 64)
		if err != nil || parsed < 4096 {
			log.Fatalf("invalid PFREPORT_MAX_BODY_BYTES: %q", raw)
		}
		maxBodyBytes = parsed
	}

	store, err := openStore(dataPath)
	if err != nil {
		log.Fatal(err)
	}
	defer store.close()

	srv := &server{
		store:        store,
		token:        os.Getenv("PFREPORT_TOKEN"),
		maxBodyBytes: maxBodyBytes,
	}

	mux := http.NewServeMux()
	mux.HandleFunc("GET /", srv.dashboard)
	mux.HandleFunc("GET /health", srv.health)
	mux.HandleFunc("GET /v1/routes", srv.routes)
	mux.HandleFunc("GET /v1/stats", srv.stats)
	mux.HandleFunc("GET /v1/recent", srv.recent)
	mux.HandleFunc("POST /v1/test", srv.ingest)
	mux.HandleFunc("POST /v1/listings", srv.ingest)
	mux.HandleFunc("GET /v1/export", srv.export)

	log.Printf("listening on %s, data=%s, loaded_hashes=%d", addr, dataPath, store.seenCount())
	if err := http.ListenAndServe(addr, mux); err != nil {
		log.Fatal(err)
	}
}

func (s *server) health(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{
		"ok":          true,
		"seenHashes":  s.store.seenCount(),
		"serverTime":  time.Now().UTC().Format(time.RFC3339Nano),
		"storagePath": s.store.dbPath(),
	})
}

func (s *server) routes(w http.ResponseWriter, r *http.Request) {
	if !s.authorized(r) {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "error": "unauthorized"})
		return
	}

	writeJSON(w, http.StatusOK, []routeInfo{
		{Method: "GET", Path: "/", Description: "dashboard"},
		{Method: "GET", Path: "/health", Description: "health check"},
		{Method: "GET", Path: "/v1/routes", Description: "route list"},
		{Method: "GET", Path: "/v1/stats", Description: "counters"},
		{Method: "GET", Path: "/v1/recent?page=1&perPage=100&q=spam", Description: "paged recent listings"},
		{Method: "POST", Path: "/v1/listings", Description: "ingest"},
		{Method: "POST", Path: "/v1/test", Description: "connectivity test"},
		{Method: "GET", Path: "/v1/export?format=csv&gzip=1", Description: "export"},
	})
}

func (s *server) stats(w http.ResponseWriter, r *http.Request) {
	if !s.authorized(r) {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "error": "unauthorized"})
		return
	}

	writeJSON(w, http.StatusOK, s.store.stats())
}

func (s *server) recent(w http.ResponseWriter, r *http.Request) {
	if !s.authorized(r) {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "error": "unauthorized"})
		return
	}

	page := queryInt(r, "page", 1)
	perPage := queryInt(r, "perPage", 0)
	if perPage == 0 {
		perPage = queryInt(r, "limit", 100)
	}

	result := s.store.page(page, perPage, r.URL.Query().Get("q"))
	writeJSON(w, http.StatusOK, map[string]any{
		"ok":       true,
		"records":  result.Records,
		"page":     result.Page,
		"perPage":  result.PerPage,
		"total":    result.Total,
		"hasMore":  result.HasMore,
		"serverAt": time.Now().UTC().Format(time.RFC3339Nano),
	})
}

func (s *server) ingest(w http.ResponseWriter, r *http.Request) {
	if !s.authorized(r) {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "error": "unauthorized"})
		return
	}

	defer r.Body.Close()
	body := http.MaxBytesReader(w, r.Body, s.maxBodyBytes)
	decoder := json.NewDecoder(body)
	decoder.DisallowUnknownFields()

	var request ingestRequest
	if err := decoder.Decode(&request); err != nil {
		status := http.StatusBadRequest
		if errors.Is(err, io.ErrUnexpectedEOF) || strings.Contains(err.Error(), "request body too large") {
			status = http.StatusRequestEntityTooLarge
		}

		writeJSON(w, status, map[string]any{"ok": false, "error": err.Error()})
		return
	}

	if request.Test || r.URL.Path == "/v1/test" {
		writeJSON(w, http.StatusOK, map[string]any{"ok": true, "test": true})
		return
	}

	if len(request.Listings) == 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "error": "listings is empty"})
		return
	}

	if len(request.Listings) > 500 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "error": "too many listings in one request"})
		return
	}

	now := time.Now().UTC()
	for i := range request.Listings {
		request.Listings[i].normalize()
		if err := request.Listings[i].validate(); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "error": fmt.Sprintf("listing %d: %s", i, err)})
			return
		}
	}

	accepted, duplicates, err := s.store.insert(request.Source, request.SentAt, now, request.Listings)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "error": err.Error()})
		return
	}

	writeJSON(w, http.StatusOK, map[string]any{
		"ok":         true,
		"accepted":   accepted,
		"duplicates": duplicates,
		"seenHashes": s.store.seenCount(),
	})
}

func (s *server) export(w http.ResponseWriter, r *http.Request) {
	if !s.authorized(r) {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "error": "unauthorized"})
		return
	}

	format := strings.ToLower(r.URL.Query().Get("format"))
	if format == "" {
		format = "ndjson"
	}

	var fileName string
	var contentType string
	var export func(io.Writer) error
	switch format {
	case "csv":
		fileName = "pfreport.csv"
		contentType = "text/csv; charset=utf-8"
		export = s.store.exportCSV
	case "ndjson", "jsonl":
		fileName = "pfreport.ndjson"
		contentType = "application/x-ndjson"
		export = s.store.exportNDJSON
	default:
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "error": "bad export format"})
		return
	}

	out := io.Writer(w)
	if wantsGzip(r) {
		fileName += ".gz"
		w.Header().Set("Content-Encoding", "gzip")
		gz := gzip.NewWriter(w)
		defer gz.Close()
		out = gz
	}

	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Content-Disposition", fmt.Sprintf("attachment; filename=%q", fileName))
	if err := export(out); err != nil {
		log.Printf("export failed: %v", err)
	}
}

func (s *server) authorized(r *http.Request) bool {
	if s.token == "" {
		return true
	}

	token := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
	if token == "" {
		token = r.Header.Get("X-Api-Key")
	}

	return subtle.ConstantTimeCompare([]byte(token), []byte(s.token)) == 1
}

func queryInt(r *http.Request, key string, fallback int) int {
	raw := r.URL.Query().Get(key)
	if raw == "" {
		return fallback
	}

	n, err := strconv.Atoi(raw)
	if err != nil {
		return fallback
	}

	return n
}

func wantsGzip(r *http.Request) bool {
	if r.URL.Query().Get("gzip") == "1" {
		return true
	}

	return strings.Contains(r.Header.Get("Accept-Encoding"), "gzip")
}

func getenv(key string, fallback string) string {
	value := os.Getenv(key)
	if value == "" {
		return fallback
	}

	return value
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

type routeInfo struct {
	Method      string `json:"method"`
	Path        string `json:"path"`
	Description string `json:"description"`
}
