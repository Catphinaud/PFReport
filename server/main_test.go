package main

import (
	"bytes"
	"compress/gzip"
	"encoding/csv"
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

func TestRecentRoutePaginates(t *testing.T) {
	store, err := openStore(filepath.Join(t.TempDir(), "pfreport.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.close()

	server := &server{store: store, maxBodyBytes: defaultMaxBodyBytes}
	now := time.Now().UTC()
	_, _, err = store.insert("test", now, now, []listing{
		{Hash: "0000000000000011", ListingID: 11, Name: "One", HomeWorld: "Ragnarok", Description: "one", SearchArea: "DataCenter", ObservedAt: now},
		{Hash: "0000000000000012", ListingID: 12, Name: "Two", HomeWorld: "Ragnarok", Description: "two", SearchArea: "DataCenter", ObservedAt: now.Add(time.Millisecond)},
		{Hash: "0000000000000013", ListingID: 13, Name: "Three", HomeWorld: "Ragnarok", Description: "three", SearchArea: "DataCenter", ObservedAt: now.Add(2 * time.Millisecond)},
	})
	if err != nil {
		t.Fatal(err)
	}

	req := httptest.NewRequest(http.MethodGet, "/v1/recent?page=2&perPage=1", nil)
	rec := httptest.NewRecorder()
	server.recent(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d", rec.Code)
	}

	var response struct {
		Records []storedRecord `json:"records"`
		Page    int            `json:"page"`
		PerPage int            `json:"perPage"`
		Total   int            `json:"total"`
		HasMore bool           `json:"hasMore"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}

	if response.Page != 2 || response.PerPage != 1 || response.Total != 3 || !response.HasMore {
		t.Fatalf("bad page response: %+v", response)
	}

	if len(response.Records) != 1 || response.Records[0].Listing.Name != "Two" {
		t.Fatalf("records = %+v", response.Records)
	}
}

func TestRecentRouteFilters(t *testing.T) {
	store, err := openStore(filepath.Join(t.TempDir(), "pfreport.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.close()

	server := &server{store: store, maxBodyBytes: defaultMaxBodyBytes}
	now := time.Now().UTC()
	_, _, err = store.insert("test", now, now, []listing{
		{Hash: "0000000000000041", ListingID: 41, DutyID: 777, Minilv: 710, Name: "Alpha", HomeWorld: "Ragnarok", Description: "fresh prog", SearchArea: "DataCenter", ObservedAt: now},
		{Hash: "0000000000000042", ListingID: 42, DutyID: 888, Minilv: 730, Name: "Beta", HomeWorld: "Cerberus", Description: "farm", SearchArea: "World", ObservedAt: now.Add(time.Millisecond)},
		{Hash: "0000000000000043", ListingID: 43, DutyID: 888, Minilv: 750, Name: "Gamma", HomeWorld: "Ragnarok", Description: "clear", SearchArea: "DataCenter", ObservedAt: now.Add(2 * time.Millisecond)},
	})
	if err != nil {
		t.Fatal(err)
	}

	req := httptest.NewRequest(http.MethodGet, "/v1/recent?world=ragnarok&dutyId=888&minilvMin=740&minilvMax=760", nil)
	rec := httptest.NewRecorder()
	server.recent(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d", rec.Code)
	}

	var response struct {
		Records []storedRecord `json:"records"`
		Total   int            `json:"total"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}

	if response.Total != 1 || len(response.Records) != 1 {
		t.Fatalf("response = %+v", response)
	}
	if response.Records[0].Listing.Name != "Gamma" {
		t.Fatalf("record = %+v", response.Records[0].Listing)
	}
}

func TestExportCSV(t *testing.T) {
	store, err := openStore(filepath.Join(t.TempDir(), "pfreport.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.close()

	server := &server{store: store, maxBodyBytes: defaultMaxBodyBytes}
	now := time.Now().UTC()
	_, _, err = store.insert("test", now, now, []listing{{
		Hash: "0000000000000021", ListingID: 21, DutyID: 777, Minilv: 710, Name: "CSV Player", HomeWorld: "Ragnarok", Description: "hello,csv", SearchArea: "DataCenter", SearchAreaRaw: 1, ObservedAt: now,
	}})
	if err != nil {
		t.Fatal(err)
	}

	req := httptest.NewRequest(http.MethodGet, "/v1/export?format=csv", nil)
	rec := httptest.NewRecorder()
	server.export(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d", rec.Code)
	}

	rows, err := csv.NewReader(bytes.NewReader(rec.Body.Bytes())).ReadAll()
	if err != nil {
		t.Fatal(err)
	}

	if len(rows) != 2 {
		t.Fatalf("rows = %d", len(rows))
	}

	if rows[1][7] != "CSV Player" || rows[1][9] != "hello,csv" {
		t.Fatalf("row = %#v", rows[1])
	}
}

func TestExportCSVFilters(t *testing.T) {
	store, err := openStore(filepath.Join(t.TempDir(), "pfreport.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.close()

	server := &server{store: store, maxBodyBytes: defaultMaxBodyBytes}
	now := time.Now().UTC()
	_, _, err = store.insert("test", now, now, []listing{
		{Hash: "0000000000000051", ListingID: 51, DutyID: 777, Minilv: 710, Name: "Low Player", HomeWorld: "Ragnarok", Description: "low", SearchArea: "DataCenter", ObservedAt: now},
		{Hash: "0000000000000052", ListingID: 52, DutyID: 888, Minilv: 750, Name: "High Player", HomeWorld: "Ragnarok", Description: "high", SearchArea: "DataCenter", ObservedAt: now.Add(time.Millisecond)},
	})
	if err != nil {
		t.Fatal(err)
	}

	req := httptest.NewRequest(http.MethodGet, "/v1/export?format=csv&minilvMin=740", nil)
	rec := httptest.NewRecorder()
	server.export(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d", rec.Code)
	}

	rows, err := csv.NewReader(bytes.NewReader(rec.Body.Bytes())).ReadAll()
	if err != nil {
		t.Fatal(err)
	}

	if len(rows) != 2 || rows[1][7] != "High Player" {
		t.Fatalf("rows = %#v", rows)
	}
}

func TestExportGzip(t *testing.T) {
	store, err := openStore(filepath.Join(t.TempDir(), "pfreport.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.close()

	server := &server{store: store, maxBodyBytes: defaultMaxBodyBytes}
	now := time.Now().UTC()
	_, _, err = store.insert("test", now, now, []listing{{
		Hash: "0000000000000031", ListingID: 31, Name: "Zip Player", HomeWorld: "Ragnarok", Description: "zip", SearchArea: "DataCenter", ObservedAt: now,
	}})
	if err != nil {
		t.Fatal(err)
	}

	req := httptest.NewRequest(http.MethodGet, "/v1/export?format=csv&gzip=1", nil)
	rec := httptest.NewRecorder()
	server.export(rec, req)

	if rec.Header().Get("Content-Encoding") != "gzip" {
		t.Fatalf("encoding = %q", rec.Header().Get("Content-Encoding"))
	}

	zr, err := gzip.NewReader(bytes.NewReader(rec.Body.Bytes()))
	if err != nil {
		t.Fatal(err)
	}
	defer zr.Close()

	rows, err := csv.NewReader(zr).ReadAll()
	if err != nil {
		t.Fatal(err)
	}

	if len(rows) != 2 || rows[1][7] != "Zip Player" {
		t.Fatalf("rows = %#v", rows)
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
