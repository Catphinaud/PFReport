package main

import (
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"hash/fnv"
	"io"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	bolt "go.etcd.io/bbolt"
)

const maxRecentRecords = 2000

var (
	recordsBucket = []byte("records")
	timeBucket    = []byte("records_by_time")
)

type store struct {
	path string
	db   *bolt.DB
}

type ingestRequest struct {
	Source   string    `json:"source"`
	SentAt   time.Time `json:"sentAt"`
	Test     bool      `json:"test"`
	Listings []listing `json:"listings"`
}

type listing struct {
	Hash          string    `json:"hash"`
	ListingID     uint64    `json:"listingId"`
	DutyID        uint16    `json:"dutyId"`
	Minilv        uint16    `json:"minilv"`
	Name          string    `json:"name"`
	HomeWorld     string    `json:"homeWorld"`
	Description   string    `json:"description"`
	SearchArea    string    `json:"searchArea"`
	SearchAreaRaw uint8     `json:"searchAreaRaw"`
	ObservedAt    time.Time `json:"observedAt"`
}

type storedRecord struct {
	ReceivedAt time.Time `json:"receivedAt"`
	Source     string    `json:"source"`
	SentAt     time.Time `json:"sentAt"`
	Listing    listing   `json:"listing"`
}

type storeStats struct {
	SeenHashes      int    `json:"seenHashes"`
	RecentAvailable int    `json:"recentAvailable"`
	RecentCapacity  int    `json:"recentCapacity"`
	StoragePath     string `json:"storagePath"`
	Database        string `json:"database"`
}

func openStore(path string) (*store, error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, err
	}

	db, err := bolt.Open(path, 0o600, &bolt.Options{Timeout: time.Second})
	if err != nil {
		return nil, err
	}

	store := &store{path: path, db: db}
	if err := store.init(); err != nil {
		_ = db.Close()
		return nil, err
	}

	return store, nil
}

func (s *store) init() error {
	return s.db.Update(func(tx *bolt.Tx) error {
		if _, err := tx.CreateBucketIfNotExists(recordsBucket); err != nil {
			return err
		}

		_, err := tx.CreateBucketIfNotExists(timeBucket)
		return err
	})
}

func (s *store) close() error {
	return s.db.Close()
}

func (s *store) dbPath() string {
	return s.path
}

func (s *store) seenCount() int {
	count := 0
	_ = s.db.View(func(tx *bolt.Tx) error {
		count = tx.Bucket(recordsBucket).Stats().KeyN
		return nil
	})

	return count
}

func (s *store) stats() storeStats {
	stats := storeStats{
		RecentCapacity: maxRecentRecords,
		StoragePath:    s.path,
		Database:       "bbolt",
	}

	_ = s.db.View(func(tx *bolt.Tx) error {
		stats.SeenHashes = tx.Bucket(recordsBucket).Stats().KeyN
		stats.RecentAvailable = min(tx.Bucket(timeBucket).Stats().KeyN, maxRecentRecords)
		return nil
	})

	return stats
}

func (s *store) recent(limit int, query string) []storedRecord {
	if limit <= 0 {
		limit = 100
	}

	if limit > maxRecentRecords {
		limit = maxRecentRecords
	}

	records := make([]storedRecord, 0, limit)
	needle := strings.ToLower(strings.TrimSpace(query))
	_ = s.db.View(func(tx *bolt.Tx) error {
		recordsByHash := tx.Bucket(recordsBucket)
		recordsByTime := tx.Bucket(timeBucket)
		cursor := recordsByTime.Cursor()

		for timeKey, hashKey := cursor.Last(); timeKey != nil && len(records) < limit; timeKey, hashKey = cursor.Prev() {
			raw := recordsByHash.Get(hashKey)
			if raw == nil {
				continue
			}

			var record storedRecord
			if err := json.Unmarshal(raw, &record); err != nil {
				continue
			}

			if needle != "" && !record.matches(needle) {
				continue
			}

			records = append(records, record)
		}

		return nil
	})

	return records
}

func (s *store) insert(source string, sentAt time.Time, receivedAt time.Time, listings []listing) (int, int, error) {
	accepted := 0
	duplicates := 0

	err := s.db.Update(func(tx *bolt.Tx) error {
		records := tx.Bucket(recordsBucket)
		recordsByTime := tx.Bucket(timeBucket)

		for _, listing := range listings {
			hashValue, err := parseHash(listing.Hash)
			if err != nil {
				return err
			}

			hk := hashKey(hashValue)
			if records.Get(hk) != nil {
				duplicates++
				continue
			}

			record := storedRecord{
				ReceivedAt: receivedAt,
				Source:     source,
				SentAt:     sentAt,
				Listing:    listing,
			}
			data, err := json.Marshal(record)
			if err != nil {
				return err
			}

			if err := records.Put(hk, data); err != nil {
				return err
			}

			if err := recordsByTime.Put(timeKey(receivedAt, hashValue), hk); err != nil {
				return err
			}

			accepted++
		}

		return nil
	})

	return accepted, duplicates, err
}

func (s *store) exportNDJSON(w io.Writer) error {
	encoder := json.NewEncoder(w)
	return s.db.View(func(tx *bolt.Tx) error {
		recordsByHash := tx.Bucket(recordsBucket)
		recordsByTime := tx.Bucket(timeBucket)
		cursor := recordsByTime.Cursor()

		for timeKey, hashKey := cursor.First(); timeKey != nil; timeKey, hashKey = cursor.Next() {
			raw := recordsByHash.Get(hashKey)
			if raw == nil {
				continue
			}

			var record storedRecord
			if err := json.Unmarshal(raw, &record); err != nil {
				return err
			}

			if err := encoder.Encode(record); err != nil {
				return err
			}
		}

		return nil
	})
}

func (r storedRecord) matches(needle string) bool {
	l := r.Listing
	return strings.Contains(strings.ToLower(l.Name), needle) ||
		strings.Contains(strings.ToLower(l.HomeWorld), needle) ||
		strings.Contains(strings.ToLower(l.Description), needle) ||
		strings.Contains(strings.ToLower(l.SearchArea), needle) ||
		strings.Contains(strings.ToLower(l.Hash), needle) ||
		strings.Contains(strconv.FormatUint(uint64(l.DutyID), 10), needle) ||
		strings.Contains(strconv.FormatUint(uint64(l.Minilv), 10), needle) ||
		strings.Contains(strings.ToLower(r.Source), needle)
}

func (l *listing) normalize() {
	l.Name = strings.TrimSpace(l.Name)
	l.HomeWorld = strings.TrimSpace(l.HomeWorld)
	l.Description = strings.TrimSpace(l.Description)
	l.SearchArea = strings.TrimSpace(l.SearchArea)
	l.Hash = strings.TrimSpace(strings.ToLower(l.Hash))

	if l.Hash == "" {
		l.Hash = computeHash(l.Name, l.HomeWorld, l.Description, l.SearchAreaRaw, l.DutyID, l.Minilv)
	}

	if l.ObservedAt.IsZero() {
		l.ObservedAt = time.Now().UTC()
	}
}

func (l listing) validate() error {
	if l.Hash == "" {
		return errors.New("hash is required")
	}

	if _, err := parseHash(l.Hash); err != nil {
		return err
	}

	if l.Name == "" {
		return errors.New("name is required")
	}

	if len(l.Name) > 128 {
		return errors.New("name is too long")
	}

	if l.HomeWorld == "" {
		return errors.New("homeWorld is required")
	}

	if len(l.HomeWorld) > 64 {
		return errors.New("homeWorld is too long")
	}

	if l.Description == "" {
		return errors.New("description is required")
	}

	if len(l.Description) > 4096 {
		return errors.New("description is too long")
	}

	if len(l.SearchArea) > 128 {
		return errors.New("searchArea is too long")
	}

	return nil
}

func parseHash(value string) (uint64, error) {
	trimmed := strings.TrimPrefix(strings.ToLower(strings.TrimSpace(value)), "0x")
	if len(trimmed) == 0 || len(trimmed) > 16 {
		return 0, fmt.Errorf("invalid hash: %q", value)
	}

	return strconv.ParseUint(trimmed, 16, 64)
}

func hashKey(hash uint64) []byte {
	key := make([]byte, 8)
	binary.BigEndian.PutUint64(key, hash)
	return key
}

func timeKey(receivedAt time.Time, hash uint64) []byte {
	key := make([]byte, 16)
	binary.BigEndian.PutUint64(key[:8], uint64(receivedAt.UnixNano()))
	binary.BigEndian.PutUint64(key[8:], hash)
	return key
}

func computeHash(name string, homeWorld string, description string, searchAreaRaw uint8, dutyID uint16, minilv uint16) string {
	h := fnv.New64a()
	writePart := func(value string) {
		_, _ = h.Write([]byte(value))
		_, _ = h.Write([]byte{0x1f})
	}
	writeUint16 := func(value uint16) {
		_, _ = h.Write([]byte{byte(value >> 8), byte(value)})
	}

	writePart(name)
	writePart(homeWorld)
	writePart(description)
	_, _ = h.Write([]byte{searchAreaRaw})
	writeUint16(dutyID)
	writeUint16(minilv)
	return fmt.Sprintf("%016x", h.Sum64())
}
