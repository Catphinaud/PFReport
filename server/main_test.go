package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"
	"time"
)

func TestIngestDedupesByHash(t *testing.T) {
	store, err := openStore(filepath.Join(t.TempDir(), "pfreport.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.close()

	server := &server{store: store, maxBodyBytes: defaultMaxBodyBytes}
	body := ingestRequest{
		Source: "test",
		SentAt: time.Now().UTC(),
		Listings: []listing{
			{
				Hash:          "00000000000000aa",
				ListingID:     1,
				DutyID:        777,
				Minilv:        710,
				Name:          "Player One",
				HomeWorld:     "Ragnarok",
				Description:   "hello",
				SearchArea:    "DataCenter",
				SearchAreaRaw: 1,
				ObservedAt:    time.Now().UTC(),
			},
			{
				Hash:          "00000000000000aa",
				ListingID:     2,
				DutyID:        888,
				Minilv:        720,
				Name:          "Player Two",
				HomeWorld:     "Ragnarok",
				Description:   "hello again",
				SearchArea:    "DataCenter",
				SearchAreaRaw: 1,
				ObservedAt:    time.Now().UTC(),
			},
		},
	}

	status, response := postJSON(t, server, "/v1/listings", body)
	if status != http.StatusOK {
		t.Fatalf("status = %d, response = %v", status, response)
	}

	if response["accepted"].(float64) != 1 {
		t.Fatalf("accepted = %v", response["accepted"])
	}

	if response["duplicates"].(float64) != 1 {
		t.Fatalf("duplicates = %v", response["duplicates"])
	}
}

func TestTokenAuth(t *testing.T) {
	store, err := openStore(filepath.Join(t.TempDir(), "pfreport.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.close()

	server := &server{store: store, token: "secret", maxBodyBytes: defaultMaxBodyBytes}
	req := httptest.NewRequest(http.MethodPost, "/v1/test", bytes.NewReader([]byte(`{"test":true,"listings":[]}`)))
	rec := httptest.NewRecorder()
	server.ingest(rec, req)

	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("status = %d", rec.Code)
	}
}

func TestRecentRouteReturnsNewestFirst(t *testing.T) {
	store, err := openStore(filepath.Join(t.TempDir(), "pfreport.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.close()

	server := &server{store: store, maxBodyBytes: defaultMaxBodyBytes}
	now := time.Now().UTC()
	_, _, err = store.insert("test", now, now, []listing{
		{
			Hash:          "0000000000000001",
			ListingID:     1,
			DutyID:        777,
			Minilv:        710,
			Name:          "First Player",
			HomeWorld:     "Ragnarok",
			Description:   "first",
			SearchArea:    "DataCenter",
			SearchAreaRaw: 1,
			ObservedAt:    now,
		},
		{
			Hash:          "0000000000000002",
			ListingID:     2,
			DutyID:        888,
			Minilv:        720,
			Name:          "Second Player",
			HomeWorld:     "Cerberus",
			Description:   "second",
			SearchArea:    "World",
			SearchAreaRaw: 8,
			ObservedAt:    now,
		},
	})
	if err != nil {
		t.Fatal(err)
	}

	req := httptest.NewRequest(http.MethodGet, "/v1/recent?limit=1", nil)
	rec := httptest.NewRecorder()
	server.recent(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d", rec.Code)
	}

	var response struct {
		Records []storedRecord `json:"records"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}

	if len(response.Records) != 1 {
		t.Fatalf("records = %d", len(response.Records))
	}

	if response.Records[0].Listing.Name != "Second Player" {
		t.Fatalf("newest record = %q", response.Records[0].Listing.Name)
	}
}

func TestDashboardRoute(t *testing.T) {
	store, err := openStore(filepath.Join(t.TempDir(), "pfreport.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.close()

	server := &server{store: store, maxBodyBytes: defaultMaxBodyBytes}
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	rec := httptest.NewRecorder()
	server.dashboard(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d", rec.Code)
	}

	if !bytes.Contains(rec.Body.Bytes(), []byte("jquery-3.7.1.min.js")) {
		t.Fatal("dashboard does not include jquery cdn")
	}
}

func postJSON(t *testing.T, server *server, path string, value any) (int, map[string]any) {
	t.Helper()

	data, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}

	req := httptest.NewRequest(http.MethodPost, path, bytes.NewReader(data))
	req.Header.Set("Content-Type", "application/json")
	rec := httptest.NewRecorder()
	server.ingest(rec, req)

	var response map[string]any
	if err := json.Unmarshal(rec.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}

	return rec.Code, response
}
